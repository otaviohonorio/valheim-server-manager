using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using ValheimServerManager.App.Helpers;
using ValheimServerManager.App.Localization;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.App.ViewModels;

public sealed record ActivityItem(string Time, string Message, string Glyph);

public sealed record SummaryItem(string Label, string Value, string Section, bool IsCustomized);

public sealed partial class DashboardViewModel : ProfilePageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly IShellService _shell;
    private DispatcherQueueTimer? _clock;

    public DashboardViewModel(
        ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs, IShellService shell)
        : base(context, manager, ui)
    {
        _dialogs = dialogs;
        _shell = shell;
    }

    public event EventHandler<string>? NavigateRequested;

    public ObservableCollection<ActivityItem> Activity { get; } = [];

    /// <summary>What the server runs with, shown next to the start button.</summary>
    public ObservableCollection<SummaryItem> Summary { get; } = [];

    public void RequestNavigation(string section) => NavigateRequested?.Invoke(this, section);

    private int _duplicateScan;
    private int _cheatScan;
    private WorldDuplicateReport? _duplicates;
    private CheatMarkReport? _cheatMarks;

    /// <summary>The world has copies placed by a second generation, or zones that would be generated again.</summary>
    [ObservableProperty]
    public partial bool HasDuplicates { get; set; }

    [ObservableProperty]
    public partial string DuplicatesText { get; set; } = string.Empty;

    /// <summary>The world has objects or items marked by the game as made with cheats.</summary>
    [ObservableProperty]
    public partial bool HasCheatMarks { get; set; }

    [ObservableProperty]
    public partial string CheatMarksText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Uptime { get; set; } = "—";

    [ObservableProperty]
    public partial string LastSaveText { get; set; } = "—";

    [ObservableProperty]
    public partial string LastBackupText { get; set; } = "—";

    [ObservableProperty]
    public partial string ModeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModeDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCreative { get; set; }

    [ObservableProperty]
    public partial string ToggleModeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorldSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorldHealthTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WorldHealthMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Microsoft.UI.Xaml.Controls.InfoBarSeverity WorldHealthSeverity { get; set; }

    [ObservableProperty]
    public partial bool CanStart { get; set; }

    [ObservableProperty]
    public partial bool CanStop { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool HasPendingChanges { get; set; }

    [ObservableProperty]
    public partial string ExitText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ConfigConfirmed { get; set; }

    [ObservableProperty]
    public partial string ConfigText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LastExitWasBad { get; set; }

    public string JoinCodeText => Status.JoinCode ?? "—";

    public string AddressText => Status.PublicAddress ?? (Profile is { } p ? string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_AddressPort, p.Port) : "—");

    public string PlayersText => Status.IsActive ? Status.PlayerCount.ToString(System.Globalization.CultureInfo.InvariantCulture) : "—";

    /// <summary>"Bjorn, Astrid" plus anyone still loading.</summary>
    public string OnlineNamesText
    {
        get
        {
            if (!Status.IsActive)
            {
                return string.Empty;
            }

            var names = string.Join(", ", Status.OnlinePlayers.Select(p => p.Name));
            var loading = Status.PlayerCount - Status.OnlinePlayers.Count;
            return loading <= 0 ? names
                : names.Length == 0 ? string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_PlayersLoading, loading)
                : string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_PlayersAndLoading, names, loading);
        }
    }

    public void StartClock()
    {
        _clock ??= Ui.CreateTimer(TimeSpan.FromSeconds(1), UpdateClock);
        _clock.Start();
    }

    protected override void OnControllerChanged()
    {
        Activity.Clear();
        if (Controller is not null)
        {
            foreach (var item in Controller.RecentActivity.Take(60))
            {
                Activity.Add(ToItem(item));
            }
        }

        RefreshWorld();
    }

    protected override void OnProfileSaved() => OnServerStatusChanged(Status);

    protected override void OnServerStatusChanged(ServerStatus status)
    {
        var profile = Profile;
        RefreshSummary(profile);
        Title = profile?.DisplayName ?? string.Empty;
        Subtitle = profile is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_Subtitle, profile.ServerName, profile.WorldName);
        IsRunning = status.State == ServerRunState.Running;
        CanStart = profile is not null && !status.IsActive && !IsBusy;
        CanStop = status.State is ServerRunState.Running or ServerRunState.Starting && !IsBusy;
        var diffs = status.IsActive ? status.ConfigDifferences : null;
        HasPendingChanges = diffs is { Count: > 0 };
        ConfigConfirmed = diffs is { Count: 0 };
        ConfigText = diffs switch
        {
            null => string.Empty,
            { Count: 0 } => string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_ConfigMatches, status.ConfigCheckedCount),
            _ => string.Join("\n", diffs.Select(d => string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_ConfigDifference, d.Setting, d.Actual, d.Expected))),
        };

        IsCreative = status.IsActive ? status.CreativeActive : profile?.IsCreativeEffective ?? false;
        ModeText = IsCreative ? DashboardStrings.Dashboard_ModeCreative : DashboardStrings.Dashboard_ModeNormal;
        ModeDetail = IsCreative
            ? DashboardStrings.Dashboard_ModeCreativeDetail
            : DashboardStrings.Dashboard_ModeNormalDetail;
        if (status.IsActive && status.ModeVerified is { } verified)
        {
            ModeDetail += " " + (verified ? DashboardStrings.Dashboard_ModeVerified : DashboardStrings.Dashboard_ModeNotVerified);
        }

        ToggleModeText = IsCreative ? DashboardStrings.Dashboard_BackToNormal : DashboardStrings.Dashboard_EnableCreative;

        LastSaveText = status.LastSaveAt is null
            ? DashboardStrings.Dashboard_NoSaveThisSession
            : SaveText(status.LastSaveNumber, status.LastSaveAt);
        LastBackupText = status.LastBackupAt is null ? LastBackupFromDisk() : $"{Helpers.Ui.Ago(status.LastBackupAt)}";

        LastExitWasBad = status is { IsActive: false, LastExit.Clean: false };
        ExitText = status.LastExit is { } exit ? $"{exit.Reason} ({Helpers.Ui.When(exit.At)})" : string.Empty;

        OnPropertyChanged(nameof(JoinCodeText));
        OnPropertyChanged(nameof(AddressText));
        OnPropertyChanged(nameof(PlayersText));
        OnPropertyChanged(nameof(OnlineNamesText));
        UpdateClock();

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        RepairWorldCommand.NotifyCanExecuteChanged();
        CleanCheatMarksCommand.NotifyCanExecuteChanged();
        if (status.State == ServerRunState.Stopped && !IsBusy)
        {
            RefreshWorld();
        }
    }

    protected override void OnActivity(ServerActivity activity)
    {
        Activity.Insert(0, ToItem(activity));
        while (Activity.Count > 60)
        {
            Activity.RemoveAt(Activity.Count - 1);
        }

        if (activity.Kind is ActivityKind.Save or ActivityKind.Backup)
        {
            RefreshWorld();
        }
    }

    protected override void OnBusyChanged(bool value) => OnServerStatusChanged(Status);

    private void RefreshSummary(Core.Profiles.ServerProfile? profile)
    {
        var lines = profile is null ? [] : Core.Profiles.ProfileSummary.Describe(profile);
        var items = lines.Select(l => new SummaryItem(l.Label, l.Value, l.Section, l.IsCustomized)).ToArray();
        if (items.SequenceEqual(Summary))
        {
            return;
        }

        Summary.Clear();
        foreach (var item in items)
        {
            Summary.Add(item);
        }
    }

    private void UpdateClock()
    {
        Uptime = Status is { IsActive: true, StartedAt: { } started } ? Helpers.Ui.Duration(DateTimeOffset.Now - started) : "—";
        if (Status.LastSaveAt is not null)
        {
            LastSaveText = SaveText(Status.LastSaveNumber, Status.LastSaveAt);
        }
    }

    private static string SaveText(object? number, DateTimeOffset? at) =>
        string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_SaveNumberAgo, number, Helpers.Ui.Ago(at));

    private string LastBackupFromDisk()
    {
        if (Profile is not { } profile)
        {
            return "—";
        }

        try
        {
            var last = Manager.Backups.List(profile, includeGameAutoBackups: false)
                .FirstOrDefault(b => b.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase) && b.Kind != BackupKind.Quarantine);
            return last is null ? DashboardStrings.Dashboard_NoBackupYet : $"{Helpers.Ui.Ago(last.CreatedAt)} ({Helpers.Ui.KindText(last.Kind).ToLowerInvariant()})";
        }
        catch (IOException)
        {
            return "—";
        }
    }

    private void RefreshWorld()
    {
        if (Profile is not { } profile || string.IsNullOrWhiteSpace(profile.SaveDirectory) || string.IsNullOrWhiteSpace(profile.WorldName))
        {
            WorldHealthTitle = DashboardStrings.Dashboard_ConfigureServerTitle;
            WorldHealthMessage = DashboardStrings.Dashboard_ConfigureServerMessage;
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
            WorldSummary = string.Empty;
            return;
        }

        WorldHealthReport report;
        try
        {
            report = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WorldHealthTitle = DashboardStrings.Dashboard_WorldUnreadableTitle;
            WorldHealthMessage = ex.Message;
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
            return;
        }

        if (report.HasErrors)
        {
            WorldHealthTitle = DashboardStrings.Dashboard_WorldBrokenTitle;
            WorldHealthMessage = string.Join(" ", report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
        }
        else if (report.Issues.Any(i => i.Code == "NEW_WORLD_SEEDED"))
        {
            WorldHealthTitle = DashboardStrings.Dashboard_NewWorldSeededTitle;
            WorldHealthMessage = report.Issues.First(i => i.Code == "NEW_WORLD_SEEDED").Message;
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational;
        }
        else if (report.IsNewWorld)
        {
            WorldHealthTitle = DashboardStrings.Dashboard_WorldMissingTitle;
            WorldHealthMessage = report.Issues.FirstOrDefault()?.Message ?? DashboardStrings.Dashboard_WorldMissingMessage;
            WorldHealthSeverity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
        }
        else
        {
            WorldHealthTitle = DashboardStrings.Dashboard_WorldHealthyTitle;
            WorldHealthMessage = string.Format(
                CultureInfo.CurrentCulture,
                DashboardStrings.Dashboard_WorldHealthyMessage,
                report.LatestSave!.Number,
                report.ChunkCount,
                Helpers.Ui.When(report.LastSavedUtc is { } u ? new DateTimeOffset(u) : null));
            WorldHealthSeverity = report.HasWarnings
                ? Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning
                : Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
        }

        if (report.IsHealthy)
        {
            _ = ScanDuplicatesAsync(profile.SaveDirectory, profile.WorldName);
            _ = ScanCheatMarksAsync(profile.SaveDirectory, profile.WorldName);
        }
        else
        {
            ShowDuplicates(null);
            ShowCheatMarks(null);
        }

        WorldSummary = report.Metadata is { } meta
            ? string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_WorldSummary, meta.SeedName, Helpers.Ui.Number(report.TotalZdos), Helpers.Ui.Size(report.SaveSetBytes))
            : string.Empty;
        LastBackupText = Status.LastBackupAt is null ? LastBackupFromDisk() : Helpers.Ui.Ago(Status.LastBackupAt);
    }

    private static ActivityItem ToItem(ServerActivity a) => new(
        a.At.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        a.Message,
        a.Kind switch
        {
            ActivityKind.Player => "",
            ActivityKind.Save => "",
            ActivityKind.Backup => "",
            ActivityKind.Warning => "",
            ActivityKind.Error => "",
            _ => "",
        });

    // ----------------------------------------------------------------- commands

    private bool CanStartServer() => CanStart;

    private bool CanStopServer() => CanStop;

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private async Task StartAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        await RunBusyAsync(DashboardStrings.Dashboard_BusyChecking, async () =>
        {
            var result = await controller.StartAsync(StartOptions.Default);
            if (result.Success)
            {
                return;
            }

            if (result.NeedsConfirmation)
            {
                var isNewWorld = result.Checks.Any(c => c.Code == "WORLD_NEW_WORLD");
                var proceed = await _dialogs.ShowChecksAsync(
                    isNewWorld ? DashboardStrings.Dashboard_NewWorldTitle : DashboardStrings.Dashboard_ConfirmStartTitle,
                    isNewWorld
                        ? string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_NewWorldMessage, controller.Profile.WorldName)
                        : DashboardStrings.Dashboard_ReadWarnings,
                    result.Checks,
                    canProceed: true,
                    proceedText: isNewWorld ? DashboardStrings.Dashboard_CreateAndStart : DashboardStrings.Dashboard_StartAnyway);
                if (proceed)
                {
                    result = await controller.StartAsync(StartOptions.Confirmed);
                    if (result.Success)
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            var worldBroken = result.Checks.Any(c => c.Code.StartsWith("WORLD_", StringComparison.Ordinal) && c.Level == CheckLevel.Blocker);
            await _dialogs.ShowChecksAsync(DashboardStrings.Dashboard_NotStartedTitle,result.Message, result.Checks, canProceed: false, proceedText: string.Empty);
            if (worldBroken)
            {
                NavigateRequested?.Invoke(this, "backups");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        if (Status.PlayerCount > 0 && !await _dialogs.ConfirmAsync(
                DashboardStrings.Dashboard_PlayersOnlineTitle,
                string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_StopPlayersMessage, Status.PlayerCount),
                DashboardStrings.Dashboard_ShutDown))
        {
            return;
        }

        await RunBusyAsync(DashboardStrings.Dashboard_BusyStopping, async () =>
        {
            var result = await controller.StopAsync();
            if (!result.Success && controller.Status.StopTimedOut)
            {
                await HandleStopTimeoutAsync(controller);
            }
            else if (!result.Success)
            {
                await _dialogs.AlertAsync(DashboardStrings.Dashboard_ShutdownTitle, result.Message);
            }
        });
    }

    private async Task HandleStopTimeoutAsync(ServerController controller)
    {
        while (controller.Status.IsActive)
        {
            var choice = await _dialogs.ChooseAsync(
                DashboardStrings.Dashboard_NotStoppedTitle,
                DashboardStrings.Dashboard_NotStoppedMessage,
                DashboardStrings.Dashboard_Wait2Minutes,
                DashboardStrings.Dashboard_ForceClose,
                DashboardStrings.Dashboard_KeepRunning);
            if (choice == 1)
            {
                BusyText = DashboardStrings.Dashboard_BusyWaitingSave;
                var result = await controller.WaitForStopAsync(TimeSpan.FromMinutes(2));
                if (result.Success || !controller.Status.IsActive)
                {
                    return;
                }
            }
            else if (choice == 2)
            {
                if (await _dialogs.ConfirmAsync(DashboardStrings.Dashboard_ForceCloseTitle, DashboardStrings.Dashboard_ForceCloseMessage, DashboardStrings.Dashboard_Force, destructive: true))
                {
                    await controller.ForceKillAsync();
                    await controller.WaitForStopAsync(TimeSpan.FromSeconds(30));
                    return;
                }
            }
            else
            {
                return;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task RestartAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        if (Status.PlayerCount > 0 && !await _dialogs.ConfirmAsync(
                DashboardStrings.Dashboard_RestartWithPlayersTitle,
                string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_RestartPlayersMessage, Status.PlayerCount),
                DashboardStrings.Dashboard_Restart))
        {
            return;
        }

        await RunBusyAsync(DashboardStrings.Dashboard_BusyRestarting, async () =>
        {
            var result = await controller.RestartAsync(StartOptions.Default);
            if (!result.Success)
            {
                await _dialogs.ShowChecksAsync(DashboardStrings.Dashboard_RestartFailedTitle,result.Message, result.Checks, false, string.Empty);
            }
        });
    }

    [RelayCommand]
    private async Task ToggleModeAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        var profile = controller.Profile;
        var goingCreative = !IsCreative;
        if (profile.Preset == Core.Profiles.WorldPreset.Hammer)
        {
            await _dialogs.AlertAsync(DashboardStrings.Dashboard_HammerTitle, DashboardStrings.Dashboard_HammerMessage);
            return;
        }

        var message = goingCreative
            ? DashboardStrings.Dashboard_CreativeOnMessage
            : DashboardStrings.Dashboard_CreativeOffMessage;
        if (Status.IsActive)
        {
            message += "\n\n" + (Status.PlayerCount > 0
                ? string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_ModeRestartPlayers, Status.PlayerCount)
                : DashboardStrings.Dashboard_ModeRestart);
        }

        if (!await _dialogs.ConfirmAsync(goingCreative ? DashboardStrings.Dashboard_EnableCreativeTitle : DashboardStrings.Dashboard_BackToNormalTitle, message,
                Status.IsActive ? DashboardStrings.Dashboard_SaveAndRestart : DashboardStrings.Dashboard_Confirm))
        {
            return;
        }

        profile.CreativeMode = goingCreative;
        Manager.SaveProfile(profile);

        if (Status.IsActive)
        {
            await RunBusyAsync(goingCreative ? DashboardStrings.Dashboard_BusyRestartingCreative : DashboardStrings.Dashboard_BusyRestartingNormal, async () =>
            {
                var result = await controller.RestartAsync(StartOptions.Default);
                if (!result.Success)
                {
                    await _dialogs.ShowChecksAsync(DashboardStrings.Dashboard_RestartFailedTitle,result.Message, result.Checks, false, string.Empty);
                }
            });
        }

        OnServerStatusChanged(Status);
    }

    private async Task ScanDuplicatesAsync(string saveDirectory, string worldName)
    {
        var version = ++_duplicateScan;
        WorldDuplicateReport? scan = null;
        try
        {
            scan = await Task.Run(() => WorldRepair.Scan(saveDirectory, worldName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The health check above already reports unreadable worlds.
        }

        if (version == _duplicateScan)
        {
            ShowDuplicates(scan);
        }
    }

    private void ShowDuplicates(WorldDuplicateReport? scan)
    {
        _duplicates = scan is { NeedsRepair: true } ? scan : null;
        HasDuplicates = _duplicates is not null;
        if (_duplicates is { } d)
        {
            var parts = new List<string>();
            if (d.ExtraCopies > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_DuplicateCopies, Helpers.Ui.Number(d.ExtraCopies), d.Summary));
            }

            if (d.ZonesWithDoubleSpawn > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_DoubleSpawnZones, d.ZonesWithDoubleSpawn));
            }

            if (d.ZonesToMark > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_ZonesToMark, d.ZonesToMark));
            }

            DuplicatesText = string.Join("; ", parts) + ". " +
                DashboardStrings.Dashboard_DuplicatesExplanation + " " +
                (Status.IsActive ? DashboardStrings.Dashboard_StopToRepair : DashboardStrings.Dashboard_RepairMakesBackup);
        }
        else
        {
            DuplicatesText = string.Empty;
        }

        RepairWorldCommand.NotifyCanExecuteChanged();
    }

    private bool CanRepairWorld() => HasDuplicates && Status.State is ServerRunState.Stopped or ServerRunState.Crashed && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRepairWorld))]
    private async Task RepairWorldAsync()
    {
        if (Controller is not { } controller || _duplicates is not { } scan)
        {
            return;
        }

        var message =
            string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_RepairConfirmWhat, Helpers.Ui.Number(scan.ExtraCopies), scan.ZonesToMark) +
            "\n\n" + DashboardStrings.Dashboard_RepairConfirmSafety;
        if (!await _dialogs.ConfirmAsync(DashboardStrings.Dashboard_RepairWorldTitle, message, DashboardStrings.Dashboard_Repair))
        {
            return;
        }

        await RunBusyAsync(DashboardStrings.Dashboard_BusyRepairing, async () =>
        {
            var result = await controller.RepairWorldAsync();
            if (result.Success)
            {
                await _dialogs.AlertAsync(DashboardStrings.Dashboard_WorldRepairedTitle, result.Message);
            }
            else
            {
                await _dialogs.ShowChecksAsync(DashboardStrings.Dashboard_WorldNotRepairedTitle,result.Message, result.Checks, false, string.Empty);
            }
        });

        RefreshWorld();
    }

    private async Task ScanCheatMarksAsync(string saveDirectory, string worldName)
    {
        var version = ++_cheatScan;
        CheatMarkReport? scan = null;
        try
        {
            scan = await Task.Run(() => WorldCheatMarks.Scan(saveDirectory, worldName));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            // The health check above already reports unreadable worlds.
        }

        if (version == _cheatScan)
        {
            ShowCheatMarks(scan);
        }
    }

    private void ShowCheatMarks(CheatMarkReport? scan)
    {
        _cheatMarks = scan is { NeedsCleaning: true } ? scan : null;
        HasCheatMarks = _cheatMarks is not null;
        if (_cheatMarks is { } marks)
        {
            var parts = new List<string>();
            if (marks.MarkedPieces > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_MarkedPieces, Helpers.Ui.Number(marks.MarkedPieces)));
            }

            if (marks.MarkedItems > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_MarkedItems, Helpers.Ui.Number(marks.MarkedItems)));
            }

            if (marks.MarkedOthers > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_MarkedOthers, Helpers.Ui.Number(marks.MarkedOthers)));
            }

            CheatMarksText = string.Format(CultureInfo.CurrentCulture, DashboardStrings.Dashboard_CheatMarksSentence, string.Join(", ", parts)) + " " +
                (Status.IsActive ? DashboardStrings.Dashboard_StopToClean : DashboardStrings.Dashboard_CleanMakesBackup);
        }
        else
        {
            CheatMarksText = string.Empty;
        }

        CleanCheatMarksCommand.NotifyCanExecuteChanged();
    }

    private bool CanCleanCheatMarks() => HasCheatMarks && Status.State is ServerRunState.Stopped or ServerRunState.Crashed && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCleanCheatMarks))]
    private async Task CleanCheatMarksAsync()
    {
        if (Controller is not { } controller || _cheatMarks is not { } marks)
        {
            return;
        }

        var message =
            string.Format(
                CultureInfo.CurrentCulture,
                DashboardStrings.Dashboard_CleanConfirmWhat,
                Helpers.Ui.Number(marks.MarkedPieces + marks.MarkedOthers),
                Helpers.Ui.Number(marks.MarkedItems)) +
            "\n\n" + DashboardStrings.Dashboard_CleanConfirmDetails;
        if (!await _dialogs.ConfirmAsync(DashboardStrings.Dashboard_CleanMarksTitle, message, DashboardStrings.Dashboard_Clean))
        {
            return;
        }

        await RunBusyAsync(DashboardStrings.Dashboard_BusyCleaning, async () =>
        {
            var result = await controller.CleanCheatMarksAsync();
            if (result.Success)
            {
                await _dialogs.AlertAsync(DashboardStrings.Dashboard_MarksRemovedTitle, result.Message);
            }
            else
            {
                await _dialogs.ShowChecksAsync(DashboardStrings.Dashboard_MarksNotRemovedTitle,result.Message, result.Checks, false, string.Empty);
            }
        });

        RefreshWorld();
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        await RunBusyAsync(DashboardStrings.Dashboard_BusyBackingUp, async () =>
        {
            try
            {
                var entry = await Manager.Backups.CreateAsync(controller.Profile, BackupKind.Manual, DashboardStrings.Dashboard_ManualBackupNote);
                controller.RecordBackup(entry);
                RefreshWorld();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                await _dialogs.AlertAsync(DashboardStrings.Dashboard_BackupFailedTitle, ex.Message);
            }
        });
    }

    [RelayCommand]
    private void OpenWorldFolder()
    {
        if (Profile is { } p)
        {
            _shell.OpenFolder(WorldFolder.WorldDirectory(p.SaveDirectory, p.WorldName));
        }
    }

    [RelayCommand]
    private void OpenLogFile()
    {
        if (Profile is { } p)
        {
            _shell.OpenFile(p.LogFilePath);
        }
    }

    [RelayCommand]
    private void CopyJoinCode()
    {
        if (Status.JoinCode is { } code)
        {
            _shell.CopyText(code);
        }
    }

    [RelayCommand]
    private void GoToBackups() => NavigateRequested?.Invoke(this, "backups");

    protected override void Dispose(bool disposing)
    {
        _clock?.Stop();
        base.Dispose(disposing);
    }
}
