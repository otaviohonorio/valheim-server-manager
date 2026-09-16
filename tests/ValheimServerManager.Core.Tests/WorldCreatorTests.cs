using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Tests;

public class WorldCreatorTests
{
    // _main.0.fwl2 written by the game's "new world" dialog (world "Troll", seed "htsE7kJL5K").
    private static readonly byte[] MenuCreatedWorld = Convert.FromHexString(
        "2e000000" + "29000000" +                // payload length 46, version 41
        "05" + "54726f6c6c" +                    // "Troll"
        "0a" + "68747345376b4a4c354b" +          // "htsE7kJL5K"
        "8efc8ecf" + "d30c0ec6ffffffff" +        // seed hash, uid
        "02000000" + "00" + "00000000" + "00000000"); // worldgen 2, never saved, no keys, no players

    [Fact]
    public void Seed_number_is_the_stable_hash_of_the_seed_text()
    {
        Assert.Equal(unchecked((int)0x8efbc41a), StableHash.Compute("SeedTeste"));
    }

    [Fact]
    public void Writes_the_same_bytes_as_the_new_world_dialog()
    {
        var game = new WorldMetadata
        {
            Version = 41,
            Name = "Troll",
            SeedName = "htsE7kJL5K",
            Seed = StableHash.Compute("htsE7kJL5K"),
            Uid = -972157741,
            WorldGenVersion = 2,
            HasBeenSaved = false,
            Keys = [],
            Players = [],
        };

        var bytes = game.ToBytes();

        Assert.Equal(50, bytes.Length);
        Assert.Equal(MenuCreatedWorld, bytes);
        var parsed = WorldMetadata.Parse(bytes);
        Assert.Equal("htsE7kJL5K", parsed.SeedName);
        Assert.False(parsed.HasBeenSaved);
        Assert.Equal(-972157741, parsed.Uid);
    }

    [Fact]
    public void Metadata_round_trips_with_keys_and_players()
    {
        var original = WorldMetadata.Parse(TestWorlds.Fwl2Bytes("MeuMundo", ["nobuildcost", "resourcerate 150"],
            [new WorldPlayer("Steam_1", "Ana", "Ana", "ABC")]));
        Assert.Equal(TestWorlds.Fwl2Bytes("MeuMundo", ["nobuildcost", "resourcerate 150"], [new WorldPlayer("Steam_1", "Ana", "Ana", "ABC")]),
            original.ToBytes());
    }

    [Fact]
    public void Created_world_is_reported_as_new_with_its_seed()
    {
        using var tmp = new TempDir();
        WorldCreator.CreateSeeded(tmp.Path, "Midgard", "Odin2026");

        var report = WorldInspector.Inspect(tmp.Path, "Midgard");

        Assert.False(report.HasErrors);
        Assert.True(report.IsNewWorld);
        Assert.Equal("Odin2026", report.Metadata!.SeedName);
        Assert.Equal(StableHash.Compute("Odin2026"), report.Metadata.Seed);
        Assert.Contains(report.Issues, i => i is { Code: "NEW_WORLD_SEEDED", Severity: IssueSeverity.Info });
    }

    [Fact]
    public void Refuses_to_overwrite_an_existing_world()
    {
        using var tmp = new TempDir();
        TestWorlds.WriteSave(WorldFolder.WorldDirectory(tmp.Path, "MeuMundo"), "MeuMundo", 3, TestWorlds.DefaultChunks);
        Assert.Throws<InvalidOperationException>(() => WorldCreator.CreateSeeded(tmp.Path, "MeuMundo", "abc"));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("SeedTeste", true)]
    [InlineData("0123456789", true)]
    [InlineData("01234567890", false)]
    [InlineData("com espaço", false)]
    [InlineData("açaí", false)]
    public void Seed_rules(string seed, bool valid)
    {
        Assert.Equal(valid, WorldSeed.Validate(seed) is null);
    }

    [Fact]
    public void Random_seeds_follow_the_game_shape()
    {
        var seeds = Enumerable.Range(0, 50).Select(_ => WorldSeed.Random()).ToArray();
        Assert.All(seeds, s => Assert.Null(WorldSeed.Validate(s)));
        Assert.All(seeds, s => Assert.Equal(10, s.Length));
        Assert.True(seeds.Distinct().Count() > 45);
    }

    [Fact]
    public void Copies_only_the_latest_complete_save_of_an_existing_world()
    {
        using var tmp = new TempDir();
        var game = tmp.Combine("game");
        var source = WorldFolder.WorldDirectory(game, "MeuMundo");
        TestWorlds.WriteSave(source, "MeuMundo", 4, [(0x1e, 0x20, 3, 10)]);
        TestWorlds.WriteSave(source, "MeuMundo", 5, TestWorlds.DefaultChunks);
        File.WriteAllBytes(Path.Combine(source, "cacheMinimapMeta"), [1]);
        var before = Directory.GetFiles(source).Select(File.ReadAllBytes).ToArray();

        var report = WorldCreator.CopyExisting(source, tmp.Combine("server"));

        Assert.True(report.IsHealthy);
        Assert.Equal(5, report.LatestSave!.Number);
        Assert.Single(WorldFolder.ScanSaveSets(report.Directory));
        Assert.Equal(7, Directory.GetFiles(report.Directory).Length);
        Assert.Equal(before, Directory.GetFiles(source).Select(File.ReadAllBytes).ToArray());
        Assert.Single(WorldCreator.ListWorlds(game));
    }

    [Fact]
    public void Refuses_to_copy_a_broken_world()
    {
        using var tmp = new TempDir();
        var source = WorldFolder.WorldDirectory(tmp.Combine("game"), "MeuMundo");
        TestWorlds.WriteSave(source, "MeuMundo", 5, TestWorlds.DefaultChunks, writeDb2: false);

        Assert.Throws<InvalidOperationException>(() => WorldCreator.CopyExisting(source, tmp.Combine("server")));
        Assert.Empty(WorldCreator.ListWorlds(tmp.Combine("game")));
    }
}
