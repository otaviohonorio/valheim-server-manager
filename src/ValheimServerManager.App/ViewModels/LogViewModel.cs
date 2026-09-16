using System.Collections.ObjectModel;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.ViewModels;

public sealed record LogLineItem(string Time, string Text, bool Important);

public sealed partial class LogViewModel : ProfilePageViewModel
{
    private const int Capacity = 1500;
    private readonly IShellService _shell;

    public LogViewModel(ProfileContext context, ServerManager manager, UiDispatcher ui, IShellService shell)
        : base(context, manager, ui)
    {
        _shell = shell;
    }

    public event EventHandler? LinesAppended;

    public ObservableCollection<LogLineItem> Lines { get; } = [];

    public ObservableCollection<string> ArchivedLogs { get; } = [];

    [ObservableProperty]
    public partial bool OnlyImportant { get; set; } = true;

    [ObservableProperty]
    public partial bool FollowTail { get; set; } = true;

    [ObservableProperty]
    public partial string EmptyText { get; set; } = string.Empty;

    protected override void OnControllerChanged() => Reload();

    partial void OnOnlyImportantChanged(bool value) => Reload();

    protected override void OnLog(IReadOnlyList<ServerLogLine> lines)
    {
        foreach (var line in lines)
        {
            if (!OnlyImportant || line.Important)
            {
                Lines.Add(ToItem(line));
            }
        }

        while (Lines.Count > Capacity)
        {
            Lines.RemoveAt(0);
        }

        UpdateEmpty();
        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Reload()
    {
        Lines.Clear();
        if (Controller is { } controller)
        {
            foreach (var line in controller.RecentLog.Where(l => !OnlyImportant || l.Important).TakeLast(Capacity))
            {
                Lines.Add(ToItem(line));
            }
        }

        ArchivedLogs.Clear();
        if (Profile is { } profile && Directory.Exists(profile.ArchivedLogsDirectory))
        {
            foreach (var file in Directory.GetFiles(profile.ArchivedLogsDirectory, "server-*.log").OrderByDescending(f => f).Take(30))
            {
                ArchivedLogs.Add(Path.GetFileName(file));
            }
        }

        UpdateEmpty();
        LinesAppended?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateEmpty() => EmptyText = Lines.Count > 0
        ? string.Empty
        : Status.IsActive
            ? (Status.HasLog ? "Aguardando mensagens do servidor…" : "Este servidor foi iniciado sem arquivo de log.")
            : "O log aparece aqui enquanto o servidor estiver rodando. Logs de sessões anteriores ficam na lista ao lado.";

    private static LogLineItem ToItem(ServerLogLine line) =>
        new(line.ReceivedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), line.Text, line.Important);

    [RelayCommand]
    private void OpenCurrentLog()
    {
        if (Profile is { } p)
        {
            _shell.OpenFile(p.LogFilePath);
        }
    }

    [RelayCommand]
    private void OpenArchived(string? fileName)
    {
        if (Profile is { } p && fileName is not null)
        {
            _shell.OpenFile(Path.Combine(p.ArchivedLogsDirectory, fileName));
        }
    }

    [RelayCommand]
    private void CopyAll() => _shell.CopyText(string.Join(System.Environment.NewLine, Lines.Select(l => l.Text)));
}

public sealed partial class AboutViewModel : ObservableObject
{
    private readonly ServerManager _manager;
    private readonly IShellService _shell;

    public AboutViewModel(ServerManager manager, IShellService shell)
    {
        _manager = manager;
        _shell = shell;
        NotifyPlayerJoins = manager.Settings.NotifyPlayerJoins;
        MinimizeToTray = manager.Settings.MinimizeToTrayOnClose;
    }

    public string Version =>
        typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "dev";

    public string DataDirectory => _manager.DataDirectory;

    public string DotNetVersion => System.Environment.Version.ToString();

    [ObservableProperty]
    public partial bool NotifyPlayerJoins { get; set; }

    [ObservableProperty]
    public partial bool MinimizeToTray { get; set; }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _manager.Settings.MinimizeToTrayOnClose = value;
        _manager.SaveSettings();
    }

    partial void OnNotifyPlayerJoinsChanged(bool value)
    {
        _manager.Settings.NotifyPlayerJoins = value;
        _manager.SaveSettings();
    }

    [RelayCommand]
    private void OpenDataDirectory() => _shell.OpenFolder(DataDirectory);

    [RelayCommand]
    private void OpenAppLogs() => _shell.OpenFolder(Path.Combine(DataDirectory, "logs"));
}
