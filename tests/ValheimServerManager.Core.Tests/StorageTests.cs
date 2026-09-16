using ValheimServerManager.Core.Players;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Settings;

namespace ValheimServerManager.Core.Tests;

public class PlayerListFileTests
{
    [Fact]
    public void Keeps_comments_and_ignores_blank_lines()
    {
        using var tmp = new TempDir();
        File.WriteAllLines(tmp.Combine("adminlist.txt"), ["// List admin players ID  ONE per line", "", "  765001  ", "765001"]);

        Assert.Equal(["765001"], PlayerListFile.Read(tmp.Path, PlayerListKind.Admins));

        PlayerListFile.Set(tmp.Path, PlayerListKind.Admins, "765002", true);
        var lines = File.ReadAllLines(tmp.Combine("adminlist.txt"));
        Assert.Equal(["// List admin players ID  ONE per line", "765001", "765002"], lines);

        PlayerListFile.Set(tmp.Path, PlayerListKind.Admins, "765001", false);
        Assert.Equal(["765002"], PlayerListFile.Read(tmp.Path, PlayerListKind.Admins));
    }

    [Fact]
    public void Creates_file_with_header_when_missing()
    {
        using var tmp = new TempDir();
        PlayerListFile.Set(tmp.Path, PlayerListKind.Banned, "1", true);
        Assert.StartsWith("//", File.ReadAllLines(tmp.Combine("bannedlist.txt"))[0]);
        Assert.True(PlayerListFile.Contains(tmp.Path, PlayerListKind.Banned, "1"));
        Assert.Empty(PlayerListFile.Read(tmp.Path, PlayerListKind.Permitted));
    }

    [Theory]
    [InlineData("Steam_76561190000000001", "76561190000000001")]
    [InlineData(" 76561190000000001 ", "76561190000000001")]
    [InlineData("xbox_123", "xbox_123")]
    public void Normalizes_ids(string input, string expected)
    {
        Assert.Equal(expected, PlayerListFile.NormalizeId(input));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips_profiles_and_keeps_password_encrypted_on_disk()
    {
        using var tmp = new TempDir();
        var store = new JsonSettingsStore(tmp.Path);
        var profile = new ServerProfile
        {
            DisplayName = "Principal",
            WorldName = "MeuMundo",
            Password = "senhaSuperSecreta",
            Resources = ResourceRate.More,
            CreativeMode = true,
        };
        var settings = new AppSettings { Profiles = [profile], SelectedProfileId = profile.Id };

        store.Save(settings);
        var raw = File.ReadAllText(tmp.Combine("settings.json"));
        Assert.DoesNotContain("senhaSuperSecreta", raw);
        Assert.Contains("\"resources\": \"More\"", raw);

        var loaded = store.Load();
        var p = Assert.Single(loaded.Profiles);
        Assert.Equal("senhaSuperSecreta", p.Password);
        Assert.Equal(ResourceRate.More, p.Resources);
        Assert.True(p.CreativeMode);
        Assert.Equal(profile.Id, loaded.SelectedProfileId);
    }

    [Fact]
    public void Falls_back_to_the_previous_file_when_the_current_one_is_corrupt()
    {
        using var tmp = new TempDir();
        var store = new JsonSettingsStore(tmp.Path);
        store.Save(new AppSettings { Profiles = [new ServerProfile { DisplayName = "A" }] });
        store.Save(new AppSettings { Profiles = [new ServerProfile { DisplayName = "B" }] });
        File.WriteAllText(tmp.Combine("settings.json"), "{ quebrado");

        Assert.Equal("A", Assert.Single(store.Load().Profiles).DisplayName);
    }

    [Fact]
    public void Missing_file_gives_empty_settings()
    {
        using var tmp = new TempDir();
        var settings = new JsonSettingsStore(tmp.Path).Load();
        Assert.Empty(settings.Profiles);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }
}
