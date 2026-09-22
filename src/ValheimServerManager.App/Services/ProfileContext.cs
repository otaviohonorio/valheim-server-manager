using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.Services;

/// <summary>The profile currently shown by every page.</summary>
public sealed partial class ProfileContext : ObservableObject
{
    private readonly ServerManager _manager;

    public ProfileContext(ServerManager manager)
    {
        _manager = manager;
        var initial = manager.Settings.SelectedProfileId;
        if (initial is null || manager.Profiles.All(p => p.Id != initial))
        {
            initial = manager.Profiles.FirstOrDefault()?.Id;
        }

        SelectedProfileId = initial;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Controller), nameof(Profile), nameof(HasProfile))]
    public partial Guid? SelectedProfileId { get; set; }

    public bool HasProfile => SelectedProfileId is not null;

    public ServerController? Controller =>
        SelectedProfileId is { } id && _manager.Profiles.Any(p => p.Id == id) ? _manager.GetController(id) : null;

    public ServerProfile? Profile => Controller?.Profile;

    partial void OnSelectedProfileIdChanged(Guid? value)
    {
        _manager.Settings.SelectedProfileId = value;
        _manager.SaveSettings();
    }
}

public sealed record AlertItem(AlertLevel Level, string Title, string Message, DateTimeOffset At, string? ProfileName)
{
    public Microsoft.UI.Xaml.Controls.InfoBarSeverity Severity => Level switch
    {
        AlertLevel.Success => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success,
        AlertLevel.Warning => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
        AlertLevel.Error or AlertLevel.Critical => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
        _ => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
    };

    public string FullTitle => ProfileName is null ? Title : $"{ProfileName}: {Title}";
}

/// <summary>Collects alerts from all servers, shows them in-app and as Windows notifications.</summary>
public sealed class AlertCenter
{
    private const int Capacity = 6;
    private readonly UiDispatcher _ui;
    private readonly INotificationService _notifications;

    public AlertCenter(ServerManager manager, UiDispatcher ui, INotificationService notifications)
    {
        _ui = ui;
        _notifications = notifications;
        manager.AlertRaised += (sender, alert) =>
            Publish(alert, (sender as ServerController)?.Profile.DisplayName, manager.Settings.NotifyPlayerJoins);
    }

    public ObservableCollection<AlertItem> Items { get; } = [];

    public void Publish(ServerAlert alert, string? profileName = null, bool notifyPlayers = true)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var item = new AlertItem(alert.Level, alert.Title, alert.Message, alert.At, profileName);
        _ui.Run(() =>
        {
            Items.Insert(0, item);
            while (Items.Count > Capacity)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        });

        var isPlayerNews = alert.IsPlayerNews;
        if (alert.Level is AlertLevel.Warning or AlertLevel.Error or AlertLevel.Critical || (isPlayerNews && notifyPlayers))
        {
            _notifications.Show(item.FullTitle, alert.Message, alert.Level switch
            {
                AlertLevel.Warning => BalloonKind.Warning,
                AlertLevel.Error or AlertLevel.Critical => BalloonKind.Error,
                _ => BalloonKind.Info,
            });
        }

        if (alert.Level is AlertLevel.Success or AlertLevel.Info)
        {
            _ = DismissLaterAsync(item);
        }
    }

    public void Dismiss(AlertItem item) => _ui.Run(() => Items.Remove(item));

    private async Task DismissLaterAsync(AlertItem item)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        Dismiss(item);
    }
}
