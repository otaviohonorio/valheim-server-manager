using System.Globalization;
using ValheimServerManager.Core.Localization;

namespace ValheimServerManager.Core.Profiles;

/// <summary>One line of the configuration summary shown before starting a server.</summary>
public sealed record SummaryLine(string Label, string Value, string Section, bool IsCustomized);

/// <summary>Plain-language description of what a profile will run with.</summary>
public static class ProfileSummary
{
    public const string WorldSection = "world";
    public const string ServerSection = "server";

    public static IReadOnlyList<SummaryLine> Describe(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var preset = Option(ModifierCatalog.Presets, profile.Preset);
        var presetIsNormal = profile.Preset == WorldPreset.Normal;

        var modifiers = new List<string>();
        AddModifier(modifiers, Strings.Summary_Combat, ModifierCatalog.Combat, profile.Combat, CombatLevel.Default);
        AddModifier(modifiers, Strings.Summary_DeathPenalty, ModifierCatalog.DeathPenalty, profile.DeathPenalty, DeathPenaltyLevel.Default);
        AddModifier(modifiers, Strings.Summary_Resources, ModifierCatalog.Resources, profile.Resources, ResourceRate.Default);
        AddModifier(modifiers, Strings.Summary_Raids, ModifierCatalog.Raids, profile.Raids, RaidFrequency.Default);
        AddModifier(modifiers, Strings.Summary_Portals, ModifierCatalog.Portals, profile.Portals, PortalRule.Default);

        var options = new List<string>();
        if (profile.IsCreativeEffective)
        {
            options.Add(profile.Preset == WorldPreset.Hammer && !profile.CreativeMode
                ? Strings.Summary_CreativeByHammer
                : Strings.Summary_CreativeMode);
        }

        if (profile.PlayerEvents) options.Add(Strings.Summary_PlayerEvents);
        if (profile.PassiveMobs) options.Add(Strings.Summary_PassiveMobs);
        if (profile.NoMap) options.Add(Strings.Summary_NoMap);

        var access = new List<string>
        {
            profile.Public ? Strings.Summary_Public : Strings.Summary_Private,
            string.Format(CultureInfo.CurrentCulture, Strings.Summary_Port, profile.Port),
        };
        if (profile.Crossplay)
        {
            access.Add("crossplay");
        }

        var backups = (profile.BackupBeforeStart, profile.BackupAfterStop) switch
        {
            (true, true) => Strings.Summary_BackupBoth,
            (true, false) => Strings.Summary_BackupBeforeOnly,
            (false, true) => Strings.Summary_BackupAfterOnly,
            _ => Strings.Summary_NoAutoBackup,
        };

        var lines = new List<SummaryLine>
        {
            new(Strings.Summary_PresetLabel, presetIsNormal ? preset.Label : $"{preset.Label} — {preset.Description}", WorldSection, !presetIsNormal),
            new(Strings.Summary_ModifiersLabel,
                modifiers.Count == 0
                    ? presetIsNormal ? Strings.Summary_NoModifiers : string.Format(CultureInfo.CurrentCulture, Strings.Summary_NoModifiersBeyondPreset, preset.Label)
                    : string.Join(" · ", modifiers),
                WorldSection,
                modifiers.Count > 0),
            new(Strings.Summary_WorldOptionsLabel, options.Count == 0 ? Strings.Summary_NoWorldOptions : string.Join(" · ", options), WorldSection, options.Count > 0),
            new(Strings.Summary_AccessLabel, string.Join(" · ", access), ServerSection, false),
            new(Strings.Summary_SavesLabel, string.Format(CultureInfo.CurrentCulture, Strings.Summary_SaveEvery, Interval(profile.SaveIntervalSeconds)) + " · " + backups, ServerSection, !profile.BackupBeforeStart || !profile.BackupAfterStop),
            new(Strings.Summary_MaintenanceLabel, profile.FixWorldAfterStop
                ? Strings.Summary_MaintenanceOn
                : Strings.Summary_MaintenanceOff, ServerSection, !profile.FixWorldAfterStop),
        };

        if (!string.IsNullOrWhiteSpace(profile.ExtraArguments))
        {
            lines.Add(new(Strings.Summary_ExtraArgumentsLabel, profile.ExtraArguments, ServerSection, true));
        }

        return lines;
    }

    private static ModifierOption<T> Option<T>(IReadOnlyList<ModifierOption<T>> options, T value)
        where T : struct, Enum =>
        options.First(o => EqualityComparer<T>.Default.Equals(o.Value, value));

    private static void AddModifier<T>(List<string> target, string label, IReadOnlyList<ModifierOption<T>> options, T value, T defaultValue)
        where T : struct, Enum
    {
        if (!EqualityComparer<T>.Default.Equals(value, defaultValue))
        {
            target.Add($"{label}: {Option(options, value).Label}");
        }
    }

    private static string Interval(int seconds) =>
        seconds % 3600 == 0 ? $"{seconds / 3600} h" : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
}
