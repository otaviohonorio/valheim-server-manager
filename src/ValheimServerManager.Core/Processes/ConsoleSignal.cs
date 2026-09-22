using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace ValheimServerManager.Core.Processes;

public enum SignalResult
{
    Sent = 0,
    ProcessNotFound = 2,
    AttachFailed = 3,
    GenerateFailed = 4,
    HelperFailed = 5,
}

/// <summary>
/// Delivers Ctrl+C to another console process — the only shutdown that makes valheim_server save.
/// </summary>
/// <remarks>
/// Windows only delivers console control events to processes sharing the caller's console, so the
/// sender must detach from its own console, attach to the server's, ignore the event itself and
/// raise it. That leaves the sending process in an odd state, so it always runs in a short-lived
/// helper process (see <see cref="SignalHelperCommand"/>), never inside the UI.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class ConsoleSignal
{
    /// <summary>Runs inside the throwaway helper process only.</summary>
    public static SignalResult SendCtrlCInCurrentProcess(int processId)
    {
        try
        {
            using var target = Process.GetProcessById(processId);
            if (target.HasExited)
            {
                return SignalResult.ProcessNotFound;
            }
        }
        catch (ArgumentException)
        {
            return SignalResult.ProcessNotFound;
        }

        NativeMethods.FreeConsole();
        if (!NativeMethods.AttachConsole((uint)processId))
        {
            return SignalResult.AttachFailed;
        }

        NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, true);
        var sent = NativeMethods.GenerateConsoleCtrlEvent(NativeMethods.CtrlCEvent, 0);

        // Give the event time to be dispatched before detaching.
        Thread.Sleep(750);
        NativeMethods.FreeConsole();

        return sent ? SignalResult.Sent : SignalResult.GenerateFailed;
    }
}

/// <summary>How the helper process is invoked (the app and the CLI each point to their own executable).</summary>
public sealed record SignalHelperCommand(string ExecutablePath, IReadOnlyList<string> ArgumentsPrefix)
{
    public const string AppSwitch = "--vsm-send-ctrl-c";

    public static SignalHelperCommand ForCurrentExecutable() =>
        new(System.Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path."), [AppSwitch]);

    /// <summary>
    /// Call first thing in <c>Main</c>. Returns an exit code when this process was started as the helper.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static int? TryRunHelper(string[] args)
    {
        if (args.Length != 2 || args[0] != AppSwitch)
        {
            return null;
        }

        return int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            ? (int)ConsoleSignal.SendCtrlCInCurrentProcess(pid)
            : (int)SignalResult.ProcessNotFound;
    }
}

public interface ISignalSender
{
    Task<SignalResult> SendCtrlCAsync(int processId, CancellationToken cancellationToken = default);
}

[SupportedOSPlatform("windows")]
public sealed class HelperProcessSignalSender(SignalHelperCommand command) : ISignalSender
{
    public async Task<SignalResult> SendCtrlCAsync(int processId, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(command.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in command.ArgumentsPrefix)
        {
            info.ArgumentList.Add(arg);
        }

        info.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));

        using var helper = Process.Start(info);
        if (helper is null)
        {
            return SignalResult.HelperFailed;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await helper.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            helper.Kill();
            return SignalResult.HelperFailed;
        }

        return Enum.IsDefined(typeof(SignalResult), helper.ExitCode)
            ? (SignalResult)helper.ExitCode
            : SignalResult.HelperFailed;
    }
}
