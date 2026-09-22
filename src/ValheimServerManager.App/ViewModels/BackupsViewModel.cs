using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerManager.App.Localization;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Servers;

namespace ValheimServerManager.App.ViewModels;

public sealed partial class BackupItemViewModel : ObservableObject
{
    public BackupItemViewModel(BackupEntry entry, bool matchesWorld)
    {
        Entry = entry;
        MatchesWorld = matchesWorld;
    }

    public BackupEntry Entry { get; }

    public bool MatchesWorld { get; }

    public string Title => Entry.Kind == BackupKind.Quarantine
        ? string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_ItemQuarantineTitle, Entry.WorldName)
        : string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_ItemSaveTitle, Entry.WorldName, Entry.SaveNumber?.ToString(CultureInfo.InvariantCulture) ?? "?");

    public string KindText => Helpers.Ui.KindText(Entry.Kind);

    public string Glyph => Helpers.Ui.KindGlyph(Entry.Kind);

    public string When => Helpers.Ui.When(Entry.CreatedAt);

    public string SizeText => Helpers.Ui.Size(Entry.TotalBytes);

    public string Details
    {
        get
        {
            var parts = new List<string> { KindText, SizeText };
            if (Entry.Manifest is { } m)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_ItemObjects, Helpers.Ui.Number(m.TotalZdos)));
                if (m.CreativeKey)
                {
                    parts.Add(WorldStrings.Backups_ItemCreativeMode);
                }
            }
            else if (Entry.Kind != BackupKind.GameAuto)
            {
                parts.Add(WorldStrings.Backups_ItemNoManifest);
            }

            if (!MatchesWorld)
            {
                parts.Add(WorldStrings.Backups_ItemOtherWorld);
            }

            return string.Join(" · ", parts);
        }
    }

    public string Note => Entry.Manifest?.Note ?? string.Empty;

    public bool CanRestore => MatchesWorld && Entry.Kind != BackupKind.Quarantine;

    public bool CanDelete => Entry.Kind != BackupKind.GameAuto;

    [ObservableProperty]
    public partial string VerifyText { get; set; } = string.Empty;
}

public sealed partial class BackupsViewModel : ProfilePageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;

    public BackupsViewModel(ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs, IShellService shell)
        : base(context, manager, ui)
    {
        _dialogs = dialogs;
        _shell = shell;
    }

    public ObservableCollection<BackupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool ShowAllWorlds { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BackupRoot { get; set; } = string.Empty;

    protected override void OnControllerChanged() => Refresh();

    protected override void OnActivity(ServerActivity activity)
    {
        if (activity.Kind == ActivityKind.Backup)
        {
            Refresh();
        }
    }

    partial void OnShowAllWorldsChanged(bool value) => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        Items.Clear();
        if (Profile is not { } profile || string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            Summary = WorldStrings.Backups_ConfigureSaveFolder;
            return;
        }

        BackupRoot = profile.EffectiveBackupDirectory;
        IReadOnlyList<BackupEntry> entries;
        try
        {
            entries = Manager.Backups.List(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Summary = string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_ListFailed, ex.Message);
            return;
        }

        foreach (var entry in entries)
        {
            var matches = entry.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase);
            if (matches || ShowAllWorlds)
            {
                Items.Add(new BackupItemViewModel(entry, matches));
            }
        }

        var mine = entries.Where(e => e.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase)).ToArray();
        Summary = mine.Length == 0
            ? string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_SummaryNone, profile.WorldName)
            : string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_Summary,
                mine.Length, profile.WorldName, Helpers.Ui.Size(mine.Sum(e => e.TotalBytes)), Helpers.Ui.When(mine[0].CreatedAt));
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        var note = await _dialogs.PromptAsync(WorldStrings.Backups_ManualTitle, WorldStrings.Backups_ManualPrompt, WorldStrings.Backups_ManualPlaceholder);
        if (note is null)
        {
            return;
        }

        await RunBusyAsync(WorldStrings.Backups_CopyingAndVerifying, async () =>
        {
            try
            {
                var entry = await Manager.Backups.CreateAsync(controller.Profile, BackupKind.Manual, string.IsNullOrWhiteSpace(note) ? null : note);
                controller.RecordBackup(entry);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await _dialogs.AlertAsync(WorldStrings.Backups_BackupFailedTitle, ex.Message);
            }
        });
        Refresh();
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupItemViewModel? item)
    {
        if (item is null || Controller is not { } controller)
        {
            return;
        }

        var profile = controller.Profile;
        var message =
            string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_RestoreQuestion, item.Title, item.When, profile.WorldName) + "\n\n" +
            WorldStrings.Backups_RestoreBulletSafetyCopy + "\n" +
            WorldStrings.Backups_RestoreBulletMoved + "\n" +
            WorldStrings.Backups_RestoreBulletVerified;

        if (controller.Status.IsActive)
        {
            message += "\n\n" + (controller.Status.PlayerCount > 0
                ? string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_RestoreServerRunningWithPlayers, controller.Status.PlayerCount)
                : WorldStrings.Backups_RestoreServerRunning);
        }

        if (!await _dialogs.ConfirmAsync(WorldStrings.Backups_RestoreTitle, message, WorldStrings.Backups_Restore))
        {
            return;
        }

        await RunBusyAsync(WorldStrings.Backups_Restoring, async () =>
        {
            if (controller.Status.IsActive)
            {
                BusyText = WorldStrings.Backups_StoppingServer;
                var stop = await controller.StopAsync();
                if (!stop.Success && controller.Status.IsActive)
                {
                    await _dialogs.AlertAsync(WorldStrings.Backups_RestoreCancelledTitle, WorldStrings.Backups_RestoreCancelledMessage);
                    return;
                }
            }

            BusyText = WorldStrings.Backups_VerifyingAndCopying;
            try
            {
                var result = await Manager.Backups.RestoreAsync(controller.Profile, item.Entry);
                var safety = result.SafetyCopy is null
                    ? string.Empty
                    : "\n\n" + string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_RestoredSafetyCopy, result.SafetyCopy.Name);
                await _dialogs.AlertAsync(WorldStrings.Backups_RestoredTitle,
                    string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_RestoredMessage, profile.WorldName, item.Entry.SaveNumber, item.When) +
                    safety + "\n\n" + WorldStrings.Backups_RestoredStartHint);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await _dialogs.AlertAsync(WorldStrings.Backups_RestoreFailedTitle, ex.Message);
            }
        });
        Refresh();
    }

    [RelayCommand]
    private async Task VerifyAsync(BackupItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        item.VerifyText = WorldStrings.Backups_Verifying;
        var result = await Manager.Backups.VerifyAsync(item.Entry);
        item.VerifyText = result.Ok ? WorldStrings.Backups_VerifyOk : "✗ " + string.Join(" ", result.Problems);
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupItemViewModel? item)
    {
        if (item is null || Profile is not { } profile)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(WorldStrings.Backups_DeleteTitle,
                string.Format(CultureInfo.CurrentCulture, WorldStrings.Backups_DeleteMessage, item.Entry.Name), WorldStrings.Backups_Delete, destructive: true))
        {
            return;
        }

        try
        {
            Manager.Backups.Delete(profile, item.Entry);
            Items.Remove(item);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            await _dialogs.AlertAsync(WorldStrings.Backups_DeleteFailedTitle, ex.Message);
        }
    }

    [RelayCommand]
    private void OpenItem(BackupItemViewModel? item)
    {
        if (item is not null)
        {
            _shell.OpenFolder(item.Entry.Directory);
        }
    }

    [RelayCommand]
    private void OpenRoot()
    {
        if (!string.IsNullOrWhiteSpace(BackupRoot))
        {
            _shell.OpenFolder(BackupRoot);
        }
    }
}
