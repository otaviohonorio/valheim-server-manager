using ValheimServerManager.Core.Localization;

namespace ValheimServerManager.Core.Profiles;

public enum WorldPreset { Normal, Casual, Easy, Hard, Hardcore, Immersive, Hammer }

public enum CombatLevel { VeryEasy, Easy, Default, Hard, VeryHard }

public enum DeathPenaltyLevel { Casual, VeryEasy, Easy, Default, Hard, Hardcore }

public enum ResourceRate { MuchLess, Less, Default, More, MuchMore, Most }

public enum RaidFrequency { None, MuchLess, Less, Default, More, MuchMore }

public enum PortalRule { Casual, Default, Hard, VeryHard }

/// <summary>A selectable option with the exact token Valheim expects on the command line.</summary>
public sealed record ModifierOption<T>(T Value, string Token, string Label, string Description)
    where T : struct, Enum;

/// <summary>
/// Every world modifier accepted by the dedicated server, with localized labels for the UI.
/// </summary>
public static class ModifierCatalog
{
    public static readonly IReadOnlyList<ModifierOption<WorldPreset>> Presets =
    [
        new(WorldPreset.Normal, "normal", Strings.Modifier_Preset_Normal, Strings.Modifier_Preset_Normal_Desc),
        new(WorldPreset.Casual, "casual", Strings.Modifier_Preset_Casual, Strings.Modifier_Preset_Casual_Desc),
        new(WorldPreset.Easy, "easy", Strings.Modifier_Preset_Easy, Strings.Modifier_Preset_Easy_Desc),
        new(WorldPreset.Hard, "hard", Strings.Modifier_Preset_Hard, Strings.Modifier_Preset_Hard_Desc),
        new(WorldPreset.Hardcore, "hardcore", Strings.Modifier_Preset_Hardcore, Strings.Modifier_Preset_Hardcore_Desc),
        new(WorldPreset.Immersive, "immersive", Strings.Modifier_Preset_Immersive, Strings.Modifier_Preset_Immersive_Desc),
        new(WorldPreset.Hammer, "hammer", Strings.Modifier_Preset_Hammer, Strings.Modifier_Preset_Hammer_Desc),
    ];

    public static readonly IReadOnlyList<ModifierOption<CombatLevel>> Combat =
    [
        new(CombatLevel.VeryEasy, "veryeasy", Strings.Modifier_Combat_VeryEasy, Strings.Modifier_Combat_VeryEasy_Desc),
        new(CombatLevel.Easy, "easy", Strings.Modifier_Combat_Easy, Strings.Modifier_Combat_Easy_Desc),
        new(CombatLevel.Default, "default", Strings.Modifier_Combat_Default, Strings.Modifier_Combat_Default_Desc),
        new(CombatLevel.Hard, "hard", Strings.Modifier_Combat_Hard, Strings.Modifier_Combat_Hard_Desc),
        new(CombatLevel.VeryHard, "veryhard", Strings.Modifier_Combat_VeryHard, Strings.Modifier_Combat_VeryHard_Desc),
    ];

    public static readonly IReadOnlyList<ModifierOption<DeathPenaltyLevel>> DeathPenalty =
    [
        new(DeathPenaltyLevel.Casual, "casual", Strings.Modifier_Death_Casual, Strings.Modifier_Death_Casual_Desc),
        new(DeathPenaltyLevel.VeryEasy, "veryeasy", Strings.Modifier_Death_VeryEasy, Strings.Modifier_Death_VeryEasy_Desc),
        new(DeathPenaltyLevel.Easy, "easy", Strings.Modifier_Death_Easy, Strings.Modifier_Death_Easy_Desc),
        new(DeathPenaltyLevel.Default, "default", Strings.Modifier_Death_Default, Strings.Modifier_Death_Default_Desc),
        new(DeathPenaltyLevel.Hard, "hard", Strings.Modifier_Death_Hard, Strings.Modifier_Death_Hard_Desc),
        new(DeathPenaltyLevel.Hardcore, "hardcore", Strings.Modifier_Death_Hardcore, Strings.Modifier_Death_Hardcore_Desc),
    ];

    public static readonly IReadOnlyList<ModifierOption<ResourceRate>> Resources =
    [
        new(ResourceRate.MuchLess, "muchless", Strings.Modifier_Resources_MuchLess, Strings.Modifier_Resources_MuchLess_Desc),
        new(ResourceRate.Less, "less", Strings.Modifier_Resources_Less, Strings.Modifier_Resources_Less_Desc),
        new(ResourceRate.Default, "default", Strings.Modifier_Resources_Default, Strings.Modifier_Resources_Default_Desc),
        new(ResourceRate.More, "more", Strings.Modifier_Resources_More, Strings.Modifier_Resources_More_Desc),
        new(ResourceRate.MuchMore, "muchmore", Strings.Modifier_Resources_MuchMore, Strings.Modifier_Resources_MuchMore_Desc),
        new(ResourceRate.Most, "most", Strings.Modifier_Resources_Most, Strings.Modifier_Resources_Most_Desc),
    ];

    public static readonly IReadOnlyList<ModifierOption<RaidFrequency>> Raids =
    [
        new(RaidFrequency.None, "none", Strings.Modifier_Raids_None, Strings.Modifier_Raids_None_Desc),
        new(RaidFrequency.MuchLess, "muchless", Strings.Modifier_Raids_MuchLess, Strings.Modifier_Raids_MuchLess_Desc),
        new(RaidFrequency.Less, "less", Strings.Modifier_Raids_Less, Strings.Modifier_Raids_Less_Desc),
        new(RaidFrequency.Default, "default", Strings.Modifier_Raids_Default, Strings.Modifier_Raids_Default_Desc),
        new(RaidFrequency.More, "more", Strings.Modifier_Raids_More, Strings.Modifier_Raids_More_Desc),
        new(RaidFrequency.MuchMore, "muchmore", Strings.Modifier_Raids_MuchMore, Strings.Modifier_Raids_MuchMore_Desc),
    ];

    public static readonly IReadOnlyList<ModifierOption<PortalRule>> Portals =
    [
        new(PortalRule.Casual, "casual", Strings.Modifier_Portals_Casual, Strings.Modifier_Portals_Casual_Desc),
        new(PortalRule.Default, "default", Strings.Modifier_Portals_Default, Strings.Modifier_Portals_Default_Desc),
        new(PortalRule.Hard, "hard", Strings.Modifier_Portals_Hard, Strings.Modifier_Portals_Hard_Desc),
        new(PortalRule.VeryHard, "veryhard", Strings.Modifier_Portals_VeryHard, Strings.Modifier_Portals_VeryHard_Desc),
    ];

    public static string Token<T>(IReadOnlyList<ModifierOption<T>> options, T value)
        where T : struct, Enum =>
        options.First(o => EqualityComparer<T>.Default.Equals(o.Value, value)).Token;

    public static bool TryParse<T>(IReadOnlyList<ModifierOption<T>> options, string token, out T value)
        where T : struct, Enum
    {
        var match = options.FirstOrDefault(o => o.Token.Equals(token, StringComparison.OrdinalIgnoreCase));
        value = match?.Value ?? default;
        return match is not null;
    }
}

/// <summary>Boolean world keys passed with <c>-setkey</c>.</summary>
public static class WorldKeys
{
    public const string NoBuildCost = "nobuildcost";
    public const string PlayerEvents = "playerevents";
    public const string PassiveMobs = "passivemobs";
    public const string NoMap = "nomap";

    public static readonly IReadOnlyList<string> All = [NoBuildCost, PlayerEvents, PassiveMobs, NoMap];
}
