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
        AddModifier(modifiers, "Combate", ModifierCatalog.Combat, profile.Combat, CombatLevel.Default);
        AddModifier(modifiers, "Penalidade de morte", ModifierCatalog.DeathPenalty, profile.DeathPenalty, DeathPenaltyLevel.Default);
        AddModifier(modifiers, "Recursos", ModifierCatalog.Resources, profile.Resources, ResourceRate.Default);
        AddModifier(modifiers, "Raids", ModifierCatalog.Raids, profile.Raids, RaidFrequency.Default);
        AddModifier(modifiers, "Portais", ModifierCatalog.Portals, profile.Portals, PortalRule.Default);

        var options = new List<string>();
        if (profile.IsCreativeEffective)
        {
            options.Add(profile.Preset == WorldPreset.Hammer && !profile.CreativeMode
                ? "Modo criativo (pelo preset Martelo)"
                : "Modo criativo — construção e craft sem custo");
        }

        if (profile.PlayerEvents) options.Add("Eventos por jogador");
        if (profile.PassiveMobs) options.Add("Inimigos passivos");
        if (profile.NoMap) options.Add("Sem mapa");

        var access = new List<string>
        {
            profile.Public ? "Público" : "Privado",
            $"porta {profile.Port}",
        };
        if (profile.Crossplay)
        {
            access.Add("crossplay");
        }

        var backups = (profile.BackupBeforeStart, profile.BackupAfterStop) switch
        {
            (true, true) => "backup antes de iniciar e depois de parar",
            (true, false) => "backup só antes de iniciar",
            (false, true) => "backup só depois de parar",
            _ => "sem backup automático",
        };

        var lines = new List<SummaryLine>
        {
            new("Preset", presetIsNormal ? preset.Label : $"{preset.Label} — {preset.Description}", WorldSection, !presetIsNormal),
            new("Modificadores",
                modifiers.Count == 0
                    ? presetIsNormal ? "nenhum (tudo no padrão)" : $"nenhum além do preset {preset.Label}"
                    : string.Join(" · ", modifiers),
                WorldSection,
                modifiers.Count > 0),
            new("Opções do mundo", options.Count == 0 ? "nenhuma" : string.Join(" · ", options), WorldSection, options.Count > 0),
            new("Acesso", string.Join(" · ", access), ServerSection, false),
            new("Saves", $"a cada {Interval(profile.SaveIntervalSeconds)} · {backups}", ServerSection, !profile.BackupBeforeStart || !profile.BackupAfterStop),
            new("Manutenção", profile.FixWorldAfterStop
                ? "corrige duplicados e marcas ao parar"
                : "desligada (o mundo não é corrigido ao parar)", ServerSection, !profile.FixWorldAfterStop),
        };

        if (!string.IsNullOrWhiteSpace(profile.ExtraArguments))
        {
            lines.Add(new("Argumentos extras", profile.ExtraArguments, ServerSection, true));
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
