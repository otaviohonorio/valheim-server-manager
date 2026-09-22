using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerManager.App.Localization;
using ValheimServerManager.App.Services;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.App.ViewModels;

public sealed record DetailItem(string Label, string Value);

public sealed record OptionItem(string Label, string Description)
{
    /// <summary>Used as the accessible name of the list item.</summary>
    public override string ToString() => Label;
}

public sealed partial class WorldViewModel : ProfileEditorViewModel
{
    private readonly IShellService _shell;

    public WorldViewModel(ProfileContext context, ServerManager manager, UiDispatcher ui, IDialogService dialogs, IShellService shell)
        : base(context, manager, ui, dialogs)
    {
        _shell = shell;
    }

    private static IReadOnlyList<ModifierOption<WorldPreset>> Presets => ModifierCatalog.Presets;
    private static IReadOnlyList<ModifierOption<CombatLevel>> CombatOptions => ModifierCatalog.Combat;
    private static IReadOnlyList<ModifierOption<DeathPenaltyLevel>> DeathOptions => ModifierCatalog.DeathPenalty;
    private static IReadOnlyList<ModifierOption<ResourceRate>> ResourceOptions => ModifierCatalog.Resources;
    private static IReadOnlyList<ModifierOption<RaidFrequency>> RaidOptions => ModifierCatalog.Raids;
    private static IReadOnlyList<ModifierOption<PortalRule>> PortalOptions => ModifierCatalog.Portals;

    public IReadOnlyList<OptionItem> PresetItems { get; } = Items(ModifierCatalog.Presets);
    public IReadOnlyList<OptionItem> CombatItems { get; } = Items(ModifierCatalog.Combat);
    public IReadOnlyList<OptionItem> DeathItems { get; } = Items(ModifierCatalog.DeathPenalty);
    public IReadOnlyList<OptionItem> ResourceItems { get; } = Items(ModifierCatalog.Resources);
    public IReadOnlyList<OptionItem> RaidItems { get; } = Items(ModifierCatalog.Raids);
    public IReadOnlyList<OptionItem> PortalItems { get; } = Items(ModifierCatalog.Portals);

    private static OptionItem[] Items<T>(IReadOnlyList<ModifierOption<T>> options)
        where T : struct, Enum =>
        options.Select(o => new OptionItem(o.Label, o.Description)).ToArray();

    public ObservableCollection<string> Worlds { get; } = [];

    public ObservableCollection<DetailItem> Details { get; } = [];

    public ObservableCollection<IssueItem> Health { get; } = [];

    public ObservableCollection<DetailItem> Buildings { get; } = [];

    [ObservableProperty] public partial string WorldName { get; set; } = string.Empty;
    [ObservableProperty] public partial int PresetIndex { get; set; }
    [ObservableProperty] public partial int CombatIndex { get; set; }
    [ObservableProperty] public partial int DeathIndex { get; set; }
    [ObservableProperty] public partial int ResourceIndex { get; set; }
    [ObservableProperty] public partial int RaidIndex { get; set; }
    [ObservableProperty] public partial int PortalIndex { get; set; }
    [ObservableProperty] public partial bool PlayerEvents { get; set; }
    [ObservableProperty] public partial bool PassiveMobs { get; set; }
    [ObservableProperty] public partial bool NoMap { get; set; }
    [ObservableProperty] public partial bool CreativeMode { get; set; }
    [ObservableProperty] public partial string PresetDescription { get; set; } = string.Empty;
    [ObservableProperty] public partial string OverrideHint { get; set; } = string.Empty;
    [ObservableProperty] public partial string BuildingsSummary { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsHammer { get; set; }

    protected override IEnumerable<string> RelevantFields => [nameof(ServerProfile.WorldName), nameof(ServerProfile.Preset)];

    protected override bool IsEditableProperty(string propertyName) =>
        propertyName is not (nameof(PresetDescription) or nameof(OverrideHint) or nameof(BuildingsSummary) or nameof(IsHammer));

    protected override void Load(ServerProfile p)
    {
        WorldName = p.WorldName;
        PresetIndex = IndexOf(Presets, p.Preset);
        CombatIndex = IndexOf(CombatOptions, p.Combat);
        DeathIndex = IndexOf(DeathOptions, p.DeathPenalty);
        ResourceIndex = IndexOf(ResourceOptions, p.Resources);
        RaidIndex = IndexOf(RaidOptions, p.Raids);
        PortalIndex = IndexOf(PortalOptions, p.Portals);
        PlayerEvents = p.PlayerEvents;
        PassiveMobs = p.PassiveMobs;
        NoMap = p.NoMap;
        CreativeMode = p.CreativeMode;
        RefreshWorlds(p);
        RefreshHealth(p);
        UpdateHints();
    }

    protected override void Apply(ServerProfile p)
    {
        p.WorldName = WorldName.Trim();
        p.Preset = Pick(Presets, PresetIndex, p.Preset);
        p.Combat = Pick(CombatOptions, CombatIndex, p.Combat);
        p.DeathPenalty = Pick(DeathOptions, DeathIndex, p.DeathPenalty);
        p.Resources = Pick(ResourceOptions, ResourceIndex, p.Resources);
        p.Raids = Pick(RaidOptions, RaidIndex, p.Raids);
        p.Portals = Pick(PortalOptions, PortalIndex, p.Portals);
        p.PlayerEvents = PlayerEvents;
        p.PassiveMobs = PassiveMobs;
        p.NoMap = NoMap;
        p.CreativeMode = CreativeMode;
    }

    protected override void OnDraftChanged()
    {
        UpdateHints();
        if (Draft() is { } draft)
        {
            RefreshHealth(draft);
        }
    }

    protected override void OnServerStatusChanged(ServerStatus status)
    {
        if (!IsDirty && status.State == ServerRunState.Stopped && Profile is { } p)
        {
            RefreshHealth(p);
        }

        FixWorldNowCommand.NotifyCanExecuteChanged();
        if (status.IsActive)
        {
            MaintenanceSummary = WorldStrings.World_StopServerToCheck;
            CanFixWorld = false;
        }
        else
        {
            _ = CheckMaintenanceAsync();
        }
    }

    private void UpdateHints()
    {
        var preset = Presets[Math.Clamp(PresetIndex, 0, Presets.Count - 1)];
        PresetDescription = preset.Description;
        IsHammer = preset.Value == WorldPreset.Hammer;
        OverrideHint = preset.Value == WorldPreset.Normal
            ? WorldStrings.World_OverrideHintNormal
            : string.Format(CultureInfo.CurrentCulture, WorldStrings.World_OverrideHintPreset, preset.Label);
    }

    private void RefreshWorlds(ServerProfile p)
    {
        Worlds.Clear();
        if (string.IsNullOrWhiteSpace(p.SaveDirectory))
        {
            return;
        }

        foreach (var world in WorldFolder.ListWorlds(p.SaveDirectory))
        {
            Worlds.Add(world);
        }
    }

    private void RefreshHealth(ServerProfile p)
    {
        Details.Clear();
        Health.Clear();
        if (string.IsNullOrWhiteSpace(p.SaveDirectory) || string.IsNullOrWhiteSpace(p.WorldName))
        {
            return;
        }

        WorldHealthReport report;
        try
        {
            report = WorldInspector.Inspect(p.SaveDirectory, p.WorldName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Health.Add(new IssueItem(ex.Message, Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning));
            return;
        }

        foreach (var issue in report.Issues)
        {
            Health.Add(new IssueItem(issue.Message, issue.Severity switch
            {
                IssueSeverity.Error => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error,
                IssueSeverity.Warning => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning,
                _ => Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational,
            }));
        }

        if (report.IsHealthy && !report.HasWarnings)
        {
            Health.Insert(0, new IssueItem(
                string.Format(CultureInfo.CurrentCulture, WorldStrings.World_SaveHealthy, report.LatestSave!.Number.ToString(CultureInfo.InvariantCulture)),
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success));
        }

        Details.Add(new(WorldStrings.World_DetailFolder, report.Directory));
        if (report.LatestSave is { } save)
        {
            Details.Add(new(WorldStrings.World_DetailCurrentSave, save.Number.ToString(CultureInfo.InvariantCulture)));
        }

        if (report.LastSavedUtc is { } saved)
        {
            Details.Add(new(WorldStrings.World_DetailSavedAt, Helpers.Ui.When(new DateTimeOffset(saved))));
        }

        if (report.Index is not null)
        {
            Details.Add(new(WorldStrings.World_DetailChunksObjects, $"{report.ChunkCount} / {Helpers.Ui.Number(report.TotalZdos)}"));
            Details.Add(new(WorldStrings.World_DetailSaveSize, Helpers.Ui.Size(report.SaveSetBytes)));
        }

        if (report.Metadata is { } meta)
        {
            Details.Add(new(WorldStrings.World_DetailInternalName, meta.Name));
            Details.Add(new(WorldStrings.World_DetailSeed, meta.SeedName));
            Details.Add(new(WorldStrings.World_DetailStoredKeys, meta.KeyNames.Any() ? string.Join(", ", meta.KeyNames) : WorldStrings.World_DetailNoKeys));
            Details.Add(new(WorldStrings.World_DetailRegisteredPlayers, meta.Players.Count > 0 ? string.Join(", ", meta.Players.Select(x => x.Name)) : WorldStrings.World_DetailNoPlayers));
        }
    }

    /// <summary>What the maintenance would do right now.</summary>
    [ObservableProperty]
    public partial string MaintenanceSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanFixWorld { get; set; }

    [RelayCommand]
    private async Task CheckMaintenanceAsync()
    {
        if (Draft() is not { } draft || string.IsNullOrWhiteSpace(draft.SaveDirectory) || string.IsNullOrWhiteSpace(draft.WorldName))
        {
            MaintenanceSummary = WorldStrings.World_ConfigureSaveFolderAndWorld;
            CanFixWorld = false;
            return;
        }

        try
        {
            var (duplicates, marks) = await Task.Run(() => (
                WorldRepair.Scan(draft.SaveDirectory, draft.WorldName),
                WorldCheatMarks.Scan(draft.SaveDirectory, draft.WorldName)));

            var parts = new List<string>();
            if (duplicates.ExtraCopies > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, WorldStrings.World_MaintenanceDuplicates, Helpers.Ui.Number(duplicates.ExtraCopies)));
            }

            if (duplicates.ZonesToMark > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, WorldStrings.World_MaintenanceZones, duplicates.ZonesToMark));
            }

            if (marks.Total > 0)
            {
                parts.Add(string.Format(CultureInfo.CurrentCulture, WorldStrings.World_MaintenanceCheatMarks,
                    Helpers.Ui.Number(marks.MarkedItems), Helpers.Ui.Number(marks.MarkedPieces + marks.MarkedOthers)));
            }

            CanFixWorld = parts.Count > 0 && !Status.IsActive;
            MaintenanceSummary = parts.Count == 0
                ? WorldStrings.World_MaintenanceNothingToFix
                : string.Format(CultureInfo.CurrentCulture, WorldStrings.World_MaintenanceToFix, string.Join(", ", parts));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            MaintenanceSummary = string.Format(CultureInfo.CurrentCulture, WorldStrings.World_MaintenanceCheckFailed, ex.Message);
            CanFixWorld = false;
        }

        FixWorldNowCommand.NotifyCanExecuteChanged();
    }

    private bool CanRunFix() => CanFixWorld && !Status.IsActive && !IsBusy;

    /// <summary>Same maintenance the manager runs after every stop, on demand.</summary>
    [RelayCommand(CanExecute = nameof(CanRunFix))]
    private async Task FixWorldNowAsync()
    {
        if (Controller is not { } controller)
        {
            return;
        }

        await RunBusyAsync(WorldStrings.World_FixingWorld, async () =>
        {
            var messages = new List<string>();
            var repair = await controller.RepairWorldAsync();
            messages.Add(repair.Message);
            var clean = await controller.CleanCheatMarksAsync();
            messages.Add(clean.Message);
            await Dialogs.AlertAsync(repair.Success && clean.Success ? WorldStrings.World_FixDoneTitle : WorldStrings.World_FixIncompleteTitle,
                string.Join("\n\n", messages));
        });

        await CheckMaintenanceAsync();
        RefreshWorldInfo();
    }

    [RelayCommand]
    private void RefreshWorldInfo()
    {
        if (Draft() is { } draft)
        {
            RefreshWorlds(draft);
            RefreshHealth(draft);
        }
    }

    [RelayCommand]
    private async Task ScanBuildingsAsync()
    {
        if (Draft() is not { } draft)
        {
            return;
        }

        Buildings.Clear();
        BuildingsSummary = string.Empty;
        await RunBusyAsync(WorldStrings.World_ScanningBuildings, async () =>
        {
            var counts = await Task.Run(() =>
            {
                var report = WorldInspector.Inspect(draft.SaveDirectory, draft.WorldName);
                return report.IsHealthy ? PlayerBuildScanner.ScanWorld(report) : null;
            });

            if (counts is null)
            {
                BuildingsSummary = WorldStrings.World_BuildingsNeedsHealthyWorld;
                return;
            }

            foreach (var (prefab, count) in counts.OrderByDescending(kv => kv.Value))
            {
                Buildings.Add(new DetailItem(prefab, Helpers.Ui.Number(count)));
            }

            BuildingsSummary = counts.Count == 0
                ? WorldStrings.World_BuildingsNoneFound
                : string.Format(CultureInfo.CurrentCulture, WorldStrings.World_BuildingsFound, Helpers.Ui.Number(counts.Values.Sum()));
        });
    }

    [RelayCommand]
    private void OpenWorldFolder()
    {
        if (Draft() is { } p && !string.IsNullOrWhiteSpace(p.SaveDirectory))
        {
            _shell.OpenFolder(WorldFolder.WorldsDirectory(p.SaveDirectory));
        }
    }

    private static int IndexOf<T>(IReadOnlyList<ModifierOption<T>> options, T value)
        where T : struct, Enum
    {
        for (var i = 0; i < options.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(options[i].Value, value))
            {
                return i;
            }
        }

        return 0;
    }

    private static T Pick<T>(IReadOnlyList<ModifierOption<T>> options, int index, T fallback)
        where T : struct, Enum =>
        index >= 0 && index < options.Count ? options[index].Value : fallback;
}
