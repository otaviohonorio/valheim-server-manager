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

        Assert.Equal("Death penalty: Casual · Resources: More (1.5x) · Portals: Casual", lines["Modifiers"].Value);
        Assert.True(lines["Modifiers"].IsCustomized);
        Assert.Equal("Normal", lines["Preset"].Value);
        Assert.Equal("none", lines["World options"].Value);
        Assert.Equal("Public · port 2456 · crossplay", lines["Access"].Value);
        Assert.Equal("every 30 min · backup before starting and after stopping", lines["Saves"].Value);
        Assert.Equal(ProfileSummary.WorldSection, lines["Modifiers"].Section);
    }

    [Fact]
    public void Default_profile_says_nothing_was_changed()
    {
        var lines = ProfileSummary.Describe(new ServerProfile()).ToDictionary(l => l.Label);
        Assert.Equal("none (all default)", lines["Modifiers"].Value);
        Assert.False(lines["Modifiers"].IsCustomized);
        Assert.DoesNotContain("Extra arguments", lines.Keys);
    }

    [Fact]
    public void Creative_and_world_options_are_listed()
    {
        var p = new ServerProfile { CreativeMode = true, PlayerEvents = true, NoMap = true, ExtraArguments = "-instanceid 2" };
        var lines = ProfileSummary.Describe(p).ToDictionary(l => l.Label);

        Assert.Equal("Creative mode — free building and crafting · Player-based events · No map", lines["World options"].Value);
        Assert.Equal("-instanceid 2", lines["Extra arguments"].Value);
    }

    [Fact]
    public void Hammer_preset_explains_creative_origin()
    {
        var lines = ProfileSummary.Describe(new ServerProfile { Preset = WorldPreset.Hammer }).ToDictionary(l => l.Label);
        Assert.StartsWith("Hammer (creative) — ", lines["Preset"].Value);
        Assert.Equal("Creative mode (from the Hammer preset)", lines["World options"].Value);
        Assert.Equal("none beyond the Hammer (creative) preset", lines["Modifiers"].Value);
    }
}
