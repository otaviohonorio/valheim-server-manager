using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Tests;

public class WorldInspectorTests
{
    [Fact]
    public void Missing_folder_means_a_new_world()
    {
        using var tmp = new TempDir();
        var report = WorldInspector.Inspect(tmp.Path, "MeuMundo");

        Assert.False(report.Exists);
        Assert.True(report.IsNewWorld);
        Assert.Contains(report.Issues, i => i.Code == "NEW_WORLD");
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Empty_folder_warns_that_a_new_world_will_be_generated()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(WorldFolder.WorldDirectory(tmp.Path, "MeuMundo"));

        var report = WorldInspector.Inspect(tmp.Path, "MeuMundo");

        Assert.True(report.Exists);
        Assert.Contains(report.Issues, i => i is { Code: "EMPTY_WORLD_FOLDER", Severity: IssueSeverity.Warning });
    }

    [Fact]
    public void Healthy_world_lists_every_file_of_the_latest_save()
    {
        using var tmp = new TempDir();
        var dir = WorldFolder.WorldDirectory(tmp.Path, "MeuMundo");
        TestWorlds.WriteSave(dir, "MeuMundo", 33, TestWorlds.DefaultChunks);
        File.WriteAllBytes(Path.Combine(dir, "cacheMinimapMeta"), [1, 2, 3]);

        var report = WorldInspector.Inspect(tmp.Path, "MeuMundo");

        Assert.True(report.IsHealthy);
        Assert.Equal(33, report.LatestSave!.Number);
        Assert.Equal(3, report.ChunkCount);
        Assert.Equal(38720, report.TotalZdos);
        Assert.Equal(7, report.SaveSetFiles.Count);
        Assert.Single(report.AuxiliaryFiles);
    }

    /// <summary>
    /// The exact state that destroyed the MeuMundo world on 2026-09-16: the newest save has its
    /// metadata but no .db2. Valheim would silently generate a new world.
    /// </summary>
    [Fact]
    public void Latest_save_without_db2_is_an_error_even_when_an_older_save_is_complete()
    {
        using var tmp = new TempDir();
        var dir = WorldFolder.WorldDirectory(tmp.Path, "MeuMundo");
        TestWorlds.WriteSave(dir, "MeuMundo", 11, TestWorlds.DefaultChunks);
        TestWorlds.WriteSave(dir, "MeuMundo", 17, [(0x1e, 0x20, 18, 20000)], writeDb2: false);

        var report = WorldInspector.Inspect(tmp.Path, "MeuMundo");

        Assert.False(report.IsHealthy);
        var issue = Assert.Single(report.Issues, i => i.Code == "INCOMPLETE_LATEST_SAVE");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("_main.17.db2", issue.Message);
        Assert.Contains("Save 11 is complete", issue.Message);
    }

    [Fact]
    public void Missing_chunk_is_an_error()
    {
        using var tmp = new TempDir();
        var dir = WorldFolder.WorldDirectory(tmp.Path, "MeuMundo");
        TestWorlds.WriteSave(dir, "MeuMundo", 5, TestWorlds.DefaultChunks);
        File.Delete(Path.Combine(dir, "20_1e__1_11.chunk"));

        var report = WorldInspector.Inspect(tmp.Path, "MeuMundo");

        Assert.Contains(report.Issues, i => i is { Code: "MISSING_CHUNK", Severity: IssueSeverity.Error });
    }

    [Fact]
    public void Chunk_with_different_object_count_is_an_error()
    {
        using var tmp = new TempDir();
        var dir = WorldFolder.WorldDirectory(tmp.Path, "MeuMundo");
        TestWorlds.WriteSave(dir, "MeuMundo", 5, TestWorlds.DefaultChunks);
        File.WriteAllBytes(Path.Combine(dir, "20_1e__1_11.chunk"), TestWorlds.ChunkBytes(1));

        var report = WorldInspector.Inspect(tmp.Path, "MeuMundo");

        Assert.Contains(report.Issues, i => i.Code == "CHUNK_COUNT_MISMATCH");
    }

    [Fact]
    public void Folder_name_with_different_case_is_a_warning()
    {
        using var tmp = new TempDir();
        var dir = WorldFolder.WorldDirectory(tmp.Path, "MeuMundo");
        TestWorlds.WriteSave(dir, "MEUMUNDO", 5, TestWorlds.DefaultChunks);

        var report = WorldInspector.Inspect(tmp.Path, "MeuMundo");

        Assert.True(report.IsHealthy);
        Assert.Contains(report.Issues, i => i is { Code: "NAME_MISMATCH", Severity: IssueSeverity.Warning });
    }

    [Fact]
    public void World_listing_skips_valheim_auto_backups()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(WorldFolder.WorldDirectory(tmp.Path, "MeuMundo"));
        Directory.CreateDirectory(WorldFolder.WorldDirectory(tmp.Path, "MeuMundo_backup_auto-20260915-213929"));
        Directory.CreateDirectory(WorldFolder.WorldDirectory(tmp.Path, "Sandbox"));

        Assert.Equal(["MeuMundo", "Sandbox"], WorldFolder.ListWorlds(tmp.Path));
        Assert.Single(WorldFolder.ListGameAutoBackups(tmp.Path, "MeuMundo"));
    }

    [Fact]
    public void Player_build_scanner_finds_player_only_pieces()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("20_1e__1_1.chunk");
        var data = TestWorlds.ChunkBytes(3, payloadBytes: 64);
        BitConverter.TryWriteBytes(data.AsSpan(10), StableHash.Compute("piece_workbench"));
        BitConverter.TryWriteBytes(data.AsSpan(30), StableHash.Compute("piece_chest_wood"));
        BitConverter.TryWriteBytes(data.AsSpan(50), StableHash.Compute("piece_chest_wood"));
        File.WriteAllBytes(path, data);

        var counts = PlayerBuildScanner.Scan(path);

        Assert.Equal(1, counts["piece_workbench"]);
        Assert.Equal(2, counts["piece_chest_wood"]);
    }
}
