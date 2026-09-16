using ValheimServerManager.Core.Profiles;

namespace ValheimServerManager.Core.Tests;

public class LaunchVerificationTests
{
    private const string Exe = "\"D:\\SteamLibrary\\steamapps\\common\\Valheim dedicated server\\valheim_server.exe\"";

    private static ServerProfile Profile() => new()
    {
        ServerDirectory = @"D:\SteamLibrary\steamapps\common\Valheim dedicated server",
        SaveDirectory = @"D:\Games\Valheim\ServerSave",
        ServerName = "Meu Servidor",
        WorldName = "MeuMundo",
        Password = "senha123",
        Port = 2456,
        Public = true,
        Crossplay = true,
        DeathPenalty = DeathPenaltyLevel.Casual,
        Resources = ResourceRate.More,
        Portals = PortalRule.Casual,
    };

    private static string Running(ServerProfile p) => Exe + " " + CommandLine.Join(LaunchArguments.Build(p));

    [Fact]
    public void Identical_configuration_has_no_differences()
    {
        var p = Profile();
        Assert.Empty(LaunchVerification.Compare(p, Running(p)));
    }

    [Fact]
    public void Every_option_is_checked()
    {
        var saved = Profile();
        var running = saved.Clone();
        running.ServerName = "Outro nome";
        running.Port = 2466;
        running.WorldName = "Sandbox";
        running.Password = "outraSenha";
        running.Public = false;
        running.Crossplay = false;
        running.SaveDirectory = @"D:\Outra";
        running.SaveIntervalSeconds = 600;
        running.GameBackupCount = 8;
        running.GameBackupShortSeconds = 3600;
        running.GameBackupLongSeconds = 21600;
        running.Preset = WorldPreset.Casual;
        running.Combat = CombatLevel.Hard;
        running.DeathPenalty = DeathPenaltyLevel.Hardcore;
        running.Resources = ResourceRate.Most;
        running.Raids = RaidFrequency.None;
        running.Portals = PortalRule.VeryHard;
        running.CreativeMode = true;
        running.PlayerEvents = true;
        running.PassiveMobs = true;
        running.NoMap = true;
        running.ExtraArguments = "-instanceid 2";

        var settings = LaunchVerification.Compare(saved, Running(running)).Select(d => d.Setting).ToArray();

        Assert.Equal(
            [
                "Nome do servidor", "Porta", "Mundo", "Senha", "Servidor público", "Crossplay", "Pasta de saves",
                "Arquivo de log", "Intervalo de save", "Backups do Valheim", "Primeiro backup do Valheim",
                "Intervalo dos backups do Valheim", "Preset",
                "Modificador deathpenalty", "Modificador resources", "Modificador portals", "Modificador combat", "Modificador raids",
                "Chave nobuildcost", "Chave playerevents", "Chave passivemobs", "Chave nomap",
                "Argumentos extras",
            ],
            settings);
    }

    [Fact]
    public void Password_values_are_never_shown()
    {
        var saved = Profile();
        var running = saved.Clone();
        running.Password = "segredoAntigo";

        var diff = Assert.Single(LaunchVerification.Compare(saved, Running(running)));

        Assert.Equal("Senha", diff.Setting);
        Assert.DoesNotContain("senha123", diff.Expected + diff.Actual);
        Assert.DoesNotContain("segredoAntigo", diff.Expected + diff.Actual);
    }

    [Fact]
    public void Old_bat_without_preset_is_flagged()
    {
        var p = Profile();
        var line = "valheim_server -nographics -batchmode -name \"Meu Servidor\" -port 2456 -world \"MeuMundo\" -password \"senha123\" " +
                   "-crossplay -public 1 -savedir \"D:\\Games\\Valheim\\ServerSave\" -logFile \"D:\\Games\\Valheim\\ServerSave\\server.log\" " +
                   "-modifier deathpenalty casual -modifier resources more -modifier portals casual";

        var diff = Assert.Single(LaunchVerification.Compare(p, line));

        Assert.Equal("Preset", diff.Setting);
        Assert.Equal("(ausente)", diff.Actual);
    }

    [Fact]
    public void Paths_compare_ignoring_case_and_trailing_separator()
    {
        var saved = Profile();
        var running = saved.Clone();
        running.SaveDirectory = @"d:\games\valheim\serversave\";
        Assert.DoesNotContain(LaunchVerification.Compare(saved, Running(running)), d => d.Setting == "Pasta de saves");
    }

    [Fact]
    public void Logged_modifiers_must_match()
    {
        var p = Profile();
        Assert.Empty(LaunchVerification.CompareLoggedModifiers(p, ["normal", "deathpenalty->casual", "resources->more", "portals->casual"]));

        var diffs = LaunchVerification.CompareLoggedModifiers(p, ["casual", "deathpenalty->casual", "raids->none"]);
        Assert.Contains(diffs, d => d.Setting == "Preset aplicado pelo servidor" && d.Actual == "casual");
        Assert.Contains(diffs, d => d.Setting.Contains("resources") && d.Actual == "(não aplicado)");
        Assert.Contains(diffs, d => d.Setting.Contains("raids") && d.Actual == "none");
    }

    [Fact]
    public void Profile_can_be_rebuilt_from_a_quoted_process_command_line()
    {
        var p = Profile();
        var rebuilt = BatchFileImporter.FromCommandLine(Running(p), out _);

        Assert.Equal("Meu Servidor", rebuilt.ServerName);
        Assert.Equal(ResourceRate.More, rebuilt.Resources);
        Assert.Empty(LaunchVerification.Compare(p, Running(rebuilt)));
    }
}
