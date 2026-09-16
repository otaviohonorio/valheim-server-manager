using ValheimServerManager.Core.Profiles;

namespace ValheimServerManager.Core.Tests;

public class LaunchArgumentsTests
{
    private static ServerProfile Sample() => new()
    {
        ServerDirectory = @"D:\Steam\Valheim dedicated server",
        SaveDirectory = @"D:\Valheim\ServerSave",
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

    [Fact]
    public void Always_passes_savedir_logfile_and_preset()
    {
        var args = LaunchArguments.Build(Sample());

        Assert.Equal(@"D:\Valheim\ServerSave", ValueAfter(args, "-savedir"));
        Assert.Equal(@"D:\Valheim\ServerSave\server.log", ValueAfter(args, "-logFile"));
        Assert.Equal("normal", ValueAfter(args, "-preset"));
    }

    [Fact]
    public void Preset_comes_before_every_modifier()
    {
        var args = LaunchArguments.Build(Sample()).ToList();
        var preset = args.IndexOf("-preset");
        var firstModifier = args.IndexOf("-modifier");

        Assert.True(preset >= 0 && firstModifier > preset);
    }

    [Fact]
    public void Reproduces_the_modifiers_of_the_original_server()
    {
        var line = CommandLine.Join(LaunchArguments.Build(Sample()));

        Assert.Contains("-modifier deathpenalty casual -modifier resources more -modifier portals casual", line);
        Assert.Contains("-crossplay", line);
        Assert.Contains("-public 1", line);
        Assert.DoesNotContain("combat", line);
        Assert.DoesNotContain("raids", line);
    }

    [Fact]
    public void Creative_mode_adds_nobuildcost_and_normal_mode_does_not()
    {
        var profile = Sample();
        Assert.DoesNotContain("nobuildcost", LaunchArguments.Build(profile));

        profile.CreativeMode = true;
        var args = LaunchArguments.Build(profile);
        Assert.Equal("nobuildcost", ValueAfter(args, "-setkey"));
        Assert.True(profile.IsCreativeEffective);
    }

    [Fact]
    public void Hammer_preset_is_always_creative()
    {
        var profile = Sample();
        profile.Preset = WorldPreset.Hammer;
        Assert.True(profile.IsCreativeEffective);
        Assert.Equal("hammer", ValueAfter(LaunchArguments.Build(profile), "-preset"));
    }

    [Fact]
    public void Display_line_masks_the_password()
    {
        var display = LaunchArguments.BuildDisplay(Sample());
        Assert.DoesNotContain("senha123", display);
        Assert.Contains("-password ••••••", display);
    }

    [Fact]
    public void Default_backup_folder_is_next_to_the_save_folder()
    {
        Assert.Equal(@"D:\Valheim\backups", Sample().EffectiveBackupDirectory);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("with space")]
    [InlineData(@"C:\Path With Spaces\")]
    [InlineData("quote\"inside")]
    [InlineData(@"back\\slashes\""mix")]
    [InlineData("")]
    public void Quote_and_split_round_trip(string value)
    {
        var line = CommandLine.Join(["-a", value, "-b"]);
        Assert.Equal(["-a", value, "-b"], CommandLine.Split(line));
    }

    private static string? ValueAfter(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}

public class ProfileValidatorTests
{
    [Fact]
    public void Valid_profile_has_no_errors()
    {
        using var tmp = new TempDir();
        var issues = ProfileValidator.Validate(tmp.Profile(), gameDataDirectory: tmp.Combine("game"));
        Assert.False(issues.HasErrors(), string.Join(" | ", issues.Select(i => i.Message)));
    }

    /// <summary>The rule born from the 2026-09-16 incident.</summary>
    [Fact]
    public void Save_folder_shared_with_the_game_is_refused()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        var gameDir = tmp.Combine("LocalLow", "IronGate", "Valheim");
        profile.SaveDirectory = gameDir + Path.DirectorySeparatorChar;

        var issues = ProfileValidator.Validate(profile, gameDir);

        Assert.Contains(issues, i => i.Field == nameof(ServerProfile.SaveDirectory) && i.Severity == ValidationSeverity.Error);
    }

    [Theory]
    [InlineData("1234", "pelo menos 5")]
    [InlineData("Servidor", "nome do servidor")]
    [InlineData("abc\"def", "aspas")]
    [InlineData("", "exige senha")]
    public void Password_rules(string password, string expectedFragment)
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        profile.ServerName = "Meu Servidor";
        profile.Password = password;

        var issues = ProfileValidator.Validate(profile, tmp.Combine("game"));

        var issue = Assert.Single(issues, i => i.Field == nameof(ServerProfile.Password));
        Assert.Contains(expectedFragment, issue.Message);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Backups_inside_worlds_local_are_refused()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        profile.BackupDirectory = Path.Combine(profile.SaveDirectory, "worlds_local", "bkp");

        var issues = ProfileValidator.Validate(profile, tmp.Combine("game"));

        Assert.Contains(issues, i => i.Field == nameof(ServerProfile.BackupDirectory) && i.Severity == ValidationSeverity.Error);
    }

    [Fact]
    public void Extra_arguments_cannot_override_managed_ones()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        profile.ExtraArguments = "-savedir C:\\outro -instanceid 2";

        var issues = ProfileValidator.Validate(profile, tmp.Combine("game"));

        Assert.Single(issues, i => i.Field == nameof(ServerProfile.ExtraArguments));
    }

    [Fact]
    public void Missing_server_executable_is_an_error()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        profile.ServerDirectory = tmp.Combine("nowhere");

        Assert.Contains(ProfileValidator.Validate(profile, tmp.Combine("game")), i => i.Field == nameof(ServerProfile.ServerDirectory));
    }
}

public class BatchFileImporterTests
{
    [Fact]
    public void Imports_the_managed_bat_layout()
    {
        string[] bat =
        [
            "@echo off",
            "setlocal",
            "title Valheim - Meu Servidor",
            "set \"SteamAppId=892970\"",
            "set \"SERVERDIR=D:\\SteamLibrary\\steamapps\\common\\Valheim dedicated server\"",
            "set \"SAVEDIR=D:\\Games\\Valheim\\ServerSave\"",
            "if not exist \"%SERVERDIR%\\valheim_server.exe\" (",
            "    echo [ERRO] valheim_server.exe nao encontrado em:",
            ")",
            "cd /d \"%SERVERDIR%\"",
            "valheim_server -nographics -batchmode -name \"Meu Servidor\" -port 2456 -world \"MeuMundo\" -password \"senha123\" -crossplay -public 1 -savedir \"%SAVEDIR%\" -logFile \"%SAVEDIR%\\server.log\" -preset normal -modifier deathpenalty casual -modifier resources more -modifier portals casual -setkey nobuildcost",
            "pause",
        ];

        var profile = BatchFileImporter.Import(bat, out var notes);

        Assert.Empty(notes);
        Assert.Equal("Meu Servidor", profile.ServerName);
        Assert.Equal("MeuMundo", profile.WorldName);
        Assert.Equal("senha123", profile.Password);
        Assert.Equal(2456, profile.Port);
        Assert.True(profile.Public);
        Assert.True(profile.Crossplay);
        Assert.True(profile.CreativeMode);
        Assert.Equal(@"D:\Games\Valheim\ServerSave", profile.SaveDirectory);
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\Valheim dedicated server", profile.ServerDirectory);
        Assert.Equal(DeathPenaltyLevel.Casual, profile.DeathPenalty);
        Assert.Equal(ResourceRate.More, profile.Resources);
        Assert.Equal(PortalRule.Casual, profile.Portals);
        Assert.Equal(string.Empty, profile.ExtraArguments);
    }

    [Fact]
    public void Steam_default_script_without_savedir_is_flagged()
    {
        string[] bat =
        [
            "@echo off",
            "set SteamAppId=892970",
            "REM Tip: Make a local copy of this script",
            "valheim_server -nographics -batchmode -name \"My server\" -port 2456 -world \"Dedicated\" -password \"secret\" -crossplay",
        ];

        var profile = BatchFileImporter.Import(bat, out var notes);

        Assert.Equal("Dedicated", profile.WorldName);
        Assert.False(profile.Public);
        Assert.Contains(notes, n => n.Contains("-savedir"));
    }

    [Fact]
    public void Unknown_arguments_are_kept_as_extras()
    {
        var profile = BatchFileImporter.Import(
            ["valheim_server.exe -name \"A\" -world W -password abcdef -instanceid 3"], out _);
        Assert.Equal("-instanceid 3", profile.ExtraArguments);
    }

    [Fact]
    public void File_without_server_line_is_rejected()
    {
        Assert.Throws<InvalidDataException>(() => BatchFileImporter.Import(["@echo off", "pause"], out _));
    }
}
