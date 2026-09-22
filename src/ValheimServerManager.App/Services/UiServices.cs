using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;

namespace ValheimServerManager.App.Services;

/// <summary>Marshals work to the UI thread. Controller events arrive on thread-pool threads.</summary>
public sealed class UiDispatcher
{
    private DispatcherQueue? _queue;

    public void Initialize(DispatcherQueue queue) => _queue = queue;

    public bool HasAccess => _queue?.HasThreadAccess ?? false;

    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_queue is null || _queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _queue.TryEnqueue(() => action());
        }
    }

    public DispatcherQueueTimer CreateTimer(TimeSpan interval, Action tick)
    {
        var queue = _queue ?? throw new InvalidOperationException("Dispatcher not initialized.");
        var timer = queue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = true;
        timer.Tick += (_, _) => tick();
        return timer;
    }
}

public interface IShellService
{
    void OpenFolder(string path);

    void OpenFile(string path);

    void RevealFile(string path);

    void CopyText(string text);
}

public sealed class ShellService(ILogger<ShellService> logger) : IShellService
{
    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Start("explorer.exe", $"\"{path}\"");
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Start("notepad.exe", $"\"{path}\"");
        }
    }

    public void RevealFile(string path) => Start("explorer.exe", $"/select,\"{path}\"");

    public void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void Start(string file, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Failed to open {File} {Args}", file, args);
        }
    }
}

public interface INotificationService
{
    void Show(string title, string message, BalloonKind kind = BalloonKind.Info);
}

/// <summary>Notifications through the tray icon (works in self-contained deployments).</summary>
public sealed class NotificationService(TrayIcon tray) : INotificationService
{
    public void Show(string title, string message, BalloonKind kind = BalloonKind.Info) =>
        tray.ShowBalloon(title, message, kind);
}
