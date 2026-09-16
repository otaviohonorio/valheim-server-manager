using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Tests;

public class ChunkIndexTests
{
    // _main.20.chunks from a real Valheim 1.0.12 save (object counts only, no player data).
    private static readonly byte[] RealIndex = Convert.FromHexString(
        "2900" + "e7150000" + "03000000" +              // version 41, 5607 objects, 3 entries
        "1e1e01" + "01000000" + "46010000" +            // x=1e z=1e rev 1, 326 objects
        "1e2001" + "01000000" + "33130000" +            // x=1e z=20 rev 1, 4915 objects
        "202001" + "01000000" + "6e010000");            // x=20 z=20 rev 1, 366 objects

    [Fact]
    public void Parses_real_index_bytes()
    {
        var index = ChunkIndex.Parse(RealIndex);

        Assert.Equal(41, index.Version);
        Assert.Equal(3, index.Entries.Count);
        Assert.Equal(5607, index.TotalZdos);
        Assert.Equal(new ChunkIndexEntry(0x1e, 0x20, 1, 1, 4915), index.Entries[1]);
    }

    [Fact]
    public void Round_trips_byte_for_byte()
    {
        var index = ChunkIndex.Parse(RealIndex);
        Assert.Equal(RealIndex, index.ToBytes());
    }

    [Fact]
    public void Entries_are_sorted_by_x_then_z()
    {
        var index = new ChunkIndex(41, [new(0x20, 0x20, 1, 1, 1), new(0x1e, 0x20, 1, 1, 1), new(0x1e, 0x1e, 1, 1, 1)]);
        Assert.Equal([(sbyte)0x1e, (sbyte)0x1e, (sbyte)0x20], index.Entries.Select(e => e.X));
        Assert.Equal([(sbyte)0x1e, (sbyte)0x20, (sbyte)0x20], index.Entries.Select(e => e.Z));
    }

    [Fact]
    public void Rejects_wrong_length()
    {
        Assert.Throws<InvalidDataException>(() => ChunkIndex.Parse(RealIndex.AsSpan(0, 40)));
    }

    [Fact]
    public void Rejects_total_that_does_not_match_entries()
    {
        var tampered = (byte[])RealIndex.Clone();
        tampered[2] = 0x00;
        Assert.Throws<InvalidDataException>(() => ChunkIndex.Parse(tampered));
    }

    [Theory]
    [InlineData(0x1e, 0x20, 1, 11, "20_1e__1_11.chunk")]
    [InlineData(0x00, 0x00, 0, 2, "00_00__0_2.chunk")]
    [InlineData(-1, 0x10, 1, 3, "10_ff__1_3.chunk")]
    public void Chunk_file_names_follow_valheim_convention(int x, int z, int flag, int rev, string expected)
    {
        var name = ChunkFileName.Format((sbyte)x, (sbyte)z, (byte)flag, rev);
        Assert.Equal(expected, name);

        Assert.True(ChunkFileName.TryParse(name, out var px, out var pz, out var pf, out var pr));
        Assert.Equal((sbyte)x, px);
        Assert.Equal((sbyte)z, pz);
        Assert.Equal((byte)flag, pf);
        Assert.Equal(rev, pr);
    }

    [Theory]
    [InlineData("cacheMinimapMask")]
    [InlineData("_main.20.db2")]
    [InlineData("20_1e_1_11.chunk")]
    public void Rejects_non_chunk_names(string name)
    {
        Assert.False(ChunkFileName.TryParse(name, out _, out _, out _, out _));
    }

    [Fact]
    public void Rebuilder_reproduces_the_index_the_game_writes()
    {
        using var tmp = new TempDir();
        TestWorlds.WriteSave(tmp.Path, "MeuMundo", 30, TestWorlds.DefaultChunks);
        var original = File.ReadAllBytes(tmp.Combine("_main.30.chunks"));

        var chunks = Directory.GetFiles(tmp.Path, "*.chunk");
        var rebuilt = ChunkIndexRebuilder.Build(chunks);

        Assert.Equal(original, rebuilt.ToBytes());
        Assert.Equal(38720, rebuilt.TotalZdos);
    }

    [Fact]
    public void Rebuilder_refuses_two_revisions_of_the_same_chunk()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(tmp.Combine("20_1e__1_11.chunk"), TestWorlds.ChunkBytes(10));
        File.WriteAllBytes(tmp.Combine("20_1e__1_12.chunk"), TestWorlds.ChunkBytes(11));

        Assert.Throws<InvalidDataException>(() => ChunkIndexRebuilder.Build(Directory.GetFiles(tmp.Path)));
    }
}

public class WorldMetadataTests
{
    [Fact]
    public void Reads_name_seed_keys_and_players()
    {
        var bytes = TestWorlds.Fwl2Bytes(
            "MeuMundo",
            ["deathkeepequip", "resourcerate 150", "preset combat_default:deathpenalty_casual", "nobuildcost"],
            [new WorldPlayer("Steam_76500000000000001", "Ana", "Ana", "ABCDEF0123456789")],
            seedName: "SeedX");

        var meta = WorldMetadata.Parse(bytes);

        Assert.Equal("MeuMundo", meta.Name);
        Assert.Equal("SeedX", meta.SeedName);
        Assert.Equal(41, meta.Version);
        Assert.True(meta.IsCreative);
        Assert.Equal(["deathkeepequip", "resourcerate", "nobuildcost"], meta.KeyNames);
        var player = Assert.Single(meta.Players);
        Assert.Equal("76500000000000001", player.SteamId);
    }

    [Fact]
    public void Normal_world_is_not_creative()
    {
        var meta = WorldMetadata.Parse(TestWorlds.Fwl2Bytes("MeuMundo", ["resourcerate 150"]));
        Assert.False(meta.IsCreative);
        Assert.Empty(meta.Players);
    }

    [Fact]
    public void Truncated_file_is_reported_as_invalid()
    {
        var bytes = TestWorlds.Fwl2Bytes("MeuMundo", ["nobuildcost"]);
        Assert.Throws<InvalidDataException>(() => WorldMetadata.Parse(bytes[..^3]));
    }

    [Theory]
    [InlineData("piece_workbench", -958010034)]
    [InlineData("wood_beam", -1109248277)]
    [InlineData("piece_chest_wood", 328745978)]
    [InlineData("a", 372029373)]
    [InlineData("", 371857150)]
    public void Stable_hash_matches_valheim(string value, int expected)
    {
        Assert.Equal(expected, StableHash.Compute(value));
    }
}
