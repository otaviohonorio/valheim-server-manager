using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
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
        var queue = _queue ?? throw new InvalidOperationException("Dispatcher não inicializado.");
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
            logger.LogWarning(ex, "Falha ao abrir {File} {Args}", file, args);
        }
    }
}

public interface INotificationService
{
    void Show(string title, string message);
}

/// <summary>Windows toast notifications; silently disabled if the system refuses registration.</summary>
public sealed class NotificationService : INotificationService, IDisposable
{
    private readonly ILogger<NotificationService> _logger;
    private readonly bool _enabled;

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, _) => App.Current?.BringToFront();
            AppNotificationManager.Default.Register();
            _enabled = true;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Notificações do Windows indisponíveis");
        }
    }

    public void Show(string title, string message)
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            var notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Falha ao mostrar notificação");
        }
    }

    public void Dispose()
    {
        if (!_enabled)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}
