using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using ValheimServerManager.Core.Localization;
using ValheimServerManager.Core.Platform;
using ValheimServerManager.Core.Profiles;

namespace ValheimServerManager.Core.Processes;

/// <summary>A valheim_server.exe found running on this machine, with its parsed command line.</summary>
public sealed record RunningServer(
    int ProcessId,
    DateTime? StartedAt,
    string? ExecutablePath,
    string CommandLine,
    string? ServerName,
    string? WorldName,
    int? Port,
    string SaveDirectory,
    bool SaveDirectoryIsDefault,
    string? LogFile,
    bool CreativeMode)
{
    public bool Matches(ServerProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.SaveDirectory) &&
        string.Equals(WorldName, profile.WorldName, StringComparison.OrdinalIgnoreCase) &&
        ValheimPaths.SameDirectory(SaveDirectory, profile.SaveDirectory);
}

public interface IServerProcessLocator
{
    IReadOnlyList<RunningServer> FindRunningServers();

    /// <summary>The command line Windows actually started the process with, or null if it is gone.</summary>
    string? GetCommandLine(int processId);

    bool IsGameRunning();
}

[SupportedOSPlatform("windows")]
public sealed class WmiServerProcessLocator : IServerProcessLocator
{
    public IReadOnlyList<RunningServer> FindRunningServers()
    {
        var result = new List<RunningServer>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, CommandLine, ExecutablePath, CreationDate FROM Win32_Process WHERE Name = 'valheim_server.exe'");
        using var collection = searcher.Get();
        foreach (var item in collection.Cast<ManagementObject>())
        {
            using (item)
            {
                var pid = Convert.ToInt32(item["ProcessId"], CultureInfo.InvariantCulture);
                var commandLine = item["CommandLine"] as string ?? string.Empty;
                var created = item["CreationDate"] is string wmiDate ? ManagementDateTimeConverter.ToDateTime(wmiDate) : (DateTime?)null;
                result.Add(Parse(pid, created, item["ExecutablePath"] as string, commandLine));
            }
        }

        return result;
    }

    public string? GetCommandLine(int processId)
    {
        using var searcher = new ManagementObjectSearcher(
            $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId.ToString(CultureInfo.InvariantCulture)}");
        using var collection = searcher.Get();
        foreach (var item in collection.Cast<ManagementObject>())
        {
            using (item)
            {
                return item["CommandLine"] as string;
            }
        }

        return null;
    }

    public bool IsGameRunning()
    {
        var processes = Process.GetProcessesByName(ValheimPaths.GameProcessName);
        foreach (var p in processes)
        {
            p.Dispose();
        }

        return processes.Length > 0;
    }

    public static RunningServer Parse(int pid, DateTime? startedAt, string? executablePath, string commandLine)
    {
        var args = Profiles.CommandLine.Split(commandLine);
        string? Value(string name)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }

        var saveDir = Value("-savedir");
        var creative = false;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if ((args[i].Equals("-setkey", StringComparison.OrdinalIgnoreCase) && args[i + 1].Equals(WorldKeys.NoBuildCost, StringComparison.OrdinalIgnoreCase)) ||
                (args[i].Equals("-preset", StringComparison.OrdinalIgnoreCase) && args[i + 1].Equals("hammer", StringComparison.OrdinalIgnoreCase)))
            {
                creative = true;
            }
        }

        return new RunningServer(
            pid,
            startedAt,
            executablePath,
            commandLine,
            Value("-name"),
            Value("-world"),
            int.TryParse(Value("-port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : null,
            saveDir ?? ValheimPaths.GameDataDirectory,
            saveDir is null,
            Value("-logFile"),
            creative);
    }
}

public interface IServerProcessLauncher
{
    Process Launch(ServerProfile profile);
}

/// <summary>
/// Starts valheim_server.exe with a console that has no window: the server behaves exactly as when
/// started from a .bat (and still receives Ctrl+C), but there is no window whose X button would kill
/// it without saving.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HiddenConsoleLauncher : IServerProcessLauncher
{
    private static readonly Lock LaunchLock = new();

    public Process Launch(ServerProfile profile)
    {
        var info = new ProcessStartInfo(profile.ServerExecutable)
        {
            WorkingDirectory = profile.ServerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["SteamAppId"] = ValheimPaths.GameAppId.ToString(CultureInfo.InvariantCulture);
        foreach (var arg in LaunchArguments.Build(profile))
        {
            info.ArgumentList.Add(arg);
        }

        // "Ignore Ctrl+C" is inherited by child processes, and hosts that start us in a new process
        // group (terminals, schedulers, IDEs) set it. A server born with it would never save on stop.
        lock (LaunchLock)
        {
            NativeMethods.SetConsoleCtrlHandler(IntPtr.Zero, false);
            return Process.Start(info) ?? throw new InvalidOperationException(Strings.Process_WindowsDidNotStart);
        }
    }
}
