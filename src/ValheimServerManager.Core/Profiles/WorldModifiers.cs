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
/// Every world modifier accepted by the dedicated server, with Portuguese labels for the UI.
/// </summary>
public static class ModifierCatalog
{
    public static readonly IReadOnlyList<ModifierOption<WorldPreset>> Presets =
    [
        new(WorldPreset.Normal, "normal", "Normal", "Experiência padrão do Valheim."),
        new(WorldPreset.Casual, "casual", "Casual", "Combate fácil, sem perder itens ao morrer, portais liberados."),
        new(WorldPreset.Easy, "easy", "Fácil", "Inimigos mais fracos."),
        new(WorldPreset.Hard, "hard", "Difícil", "Inimigos mais fortes."),
        new(WorldPreset.Hardcore, "hardcore", "Hardcore", "Morte apaga o personagem."),
        new(WorldPreset.Immersive, "immersive", "Imersivo", "Sem mapa, portais restritos."),
        new(WorldPreset.Hammer, "hammer", "Martelo (criativo)", "Construção sem custo, sem raids. Sempre criativo."),
    ];

    public static readonly IReadOnlyList<ModifierOption<CombatLevel>> Combat =
    [
        new(CombatLevel.VeryEasy, "veryeasy", "Muito fácil", "Inimigos causam bem menos dano."),
        new(CombatLevel.Easy, "easy", "Fácil", "Inimigos causam menos dano."),
        new(CombatLevel.Default, "default", "Padrão", "Dano normal."),
        new(CombatLevel.Hard, "hard", "Difícil", "Inimigos causam mais dano."),
        new(CombatLevel.VeryHard, "veryhard", "Muito difícil", "Inimigos causam muito mais dano."),
    ];

    public static readonly IReadOnlyList<ModifierOption<DeathPenaltyLevel>> DeathPenalty =
    [
        new(DeathPenaltyLevel.Casual, "casual", "Casual", "Mantém o equipamento; o resto cai. Perde 1% de habilidade."),
        new(DeathPenaltyLevel.VeryEasy, "veryeasy", "Muito fácil", "Tudo cai. Perde 1% de habilidade."),
        new(DeathPenaltyLevel.Easy, "easy", "Fácil", "Tudo cai. Perde 2,5% de habilidade."),
        new(DeathPenaltyLevel.Default, "default", "Padrão", "Tudo cai. Perde 5% de habilidade."),
        new(DeathPenaltyLevel.Hard, "hard", "Difícil", "Equipamento cai, o resto é destruído. Perde 7,5%."),
        new(DeathPenaltyLevel.Hardcore, "hardcore", "Hardcore", "Tudo é destruído. Perde 100% de habilidade."),
    ];

    public static readonly IReadOnlyList<ModifierOption<ResourceRate>> Resources =
    [
        new(ResourceRate.MuchLess, "muchless", "Muito menos (0,5x)", "Metade dos recursos."),
        new(ResourceRate.Less, "less", "Menos (0,75x)", "Três quartos dos recursos."),
        new(ResourceRate.Default, "default", "Padrão (1x)", "Quantidade normal."),
        new(ResourceRate.More, "more", "Mais (1,5x)", "50% a mais. Não afeta peixes, troféus e drops de chefe."),
        new(ResourceRate.MuchMore, "muchmore", "Muito mais (2x)", "O dobro."),
        new(ResourceRate.Most, "most", "Máximo (3x)", "O triplo."),
    ];

    public static readonly IReadOnlyList<ModifierOption<RaidFrequency>> Raids =
    [
        new(RaidFrequency.None, "none", "Nenhuma", "Sem raids (ataques noturnos continuam)."),
        new(RaidFrequency.MuchLess, "muchless", "Muito menos", "Raids bem raras."),
        new(RaidFrequency.Less, "less", "Menos", "Raids raras."),
        new(RaidFrequency.Default, "default", "Padrão", "Frequência normal."),
        new(RaidFrequency.More, "more", "Mais", "Raids frequentes."),
        new(RaidFrequency.MuchMore, "muchmore", "Muito mais", "Raids muito frequentes."),
    ];

    public static readonly IReadOnlyList<ModifierOption<PortalRule>> Portals =
    [
        new(PortalRule.Casual, "casual", "Casual", "Qualquer item passa pelo portal."),
        new(PortalRule.Default, "default", "Padrão", "Minérios e metais não passam."),
        new(PortalRule.Hard, "hard", "Difícil", "Portais só entre pontos já descobertos."),
        new(PortalRule.VeryHard, "veryhard", "Muito difícil", "Sem portais."),
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
