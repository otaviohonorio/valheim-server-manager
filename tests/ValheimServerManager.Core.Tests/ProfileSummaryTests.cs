using ValheimServerManager.Core.Profiles;

namespace ValheimServerManager.Core.Tests;

public class ProfileSummaryTests
{
    [Fact]
    public void Lists_only_the_modifiers_that_were_changed()
    {
        var p = new ServerProfile
        {
            DeathPenalty = DeathPenaltyLevel.Casual,
            Resources = ResourceRate.More,
            Portals = PortalRule.Casual,
            Port = 2456,
            Crossplay = true,
        };

        var lines = ProfileSummary.Describe(p).ToDictionary(l => l.Label);

        Assert.Equal("Penalidade de morte: Casual · Recursos: Mais (1,5x) · Portais: Casual", lines["Modificadores"].Value);
        Assert.True(lines["Modificadores"].IsCustomized);
        Assert.Equal("Normal", lines["Preset"].Value);
        Assert.Equal("nenhuma", lines["Opções do mundo"].Value);
        Assert.Equal("Público · porta 2456 · crossplay", lines["Acesso"].Value);
        Assert.Equal("a cada 30 min · backup antes de iniciar e depois de parar", lines["Saves"].Value);
        Assert.Equal(ProfileSummary.WorldSection, lines["Modificadores"].Section);
    }

    [Fact]
    public void Default_profile_says_nothing_was_changed()
    {
        var lines = ProfileSummary.Describe(new ServerProfile()).ToDictionary(l => l.Label);
        Assert.Equal("nenhum (tudo no padrão)", lines["Modificadores"].Value);
        Assert.False(lines["Modificadores"].IsCustomized);
        Assert.DoesNotContain("Argumentos extras", lines.Keys);
    }

    [Fact]
    public void Creative_and_world_options_are_listed()
    {
        var p = new ServerProfile { CreativeMode = true, PlayerEvents = true, NoMap = true, ExtraArguments = "-instanceid 2" };
        var lines = ProfileSummary.Describe(p).ToDictionary(l => l.Label);

        Assert.Equal("Modo criativo — construção e craft sem custo · Eventos por jogador · Sem mapa", lines["Opções do mundo"].Value);
        Assert.Equal("-instanceid 2", lines["Argumentos extras"].Value);
    }

    [Fact]
    public void Hammer_preset_explains_creative_origin()
    {
        var lines = ProfileSummary.Describe(new ServerProfile { Preset = WorldPreset.Hammer }).ToDictionary(l => l.Label);
        Assert.StartsWith("Martelo (criativo) — ", lines["Preset"].Value);
        Assert.Equal("Modo criativo (pelo preset Martelo)", lines["Opções do mundo"].Value);
        Assert.Equal("nenhum além do preset Martelo (criativo)", lines["Modificadores"].Value);
    }
}
