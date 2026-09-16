using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Tests;

public class BackupServiceTests
{
    private static readonly BackupService Service = new(TimeProvider.System);

    [Fact]
    public async Task Creates_a_verified_backup_with_only_the_latest_save()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        var world = WorldFolder.WorldDirectory(profile.SaveDirectory, profile.WorldName);
        TestWorlds.WriteSave(world, "MeuMundo", 10, [(0x1e, 0x20, 9, 100)]);
        TestWorlds.WriteSave(world, "MeuMundo", 11, TestWorlds.DefaultChunks, keys: ["nobuildcost"]);
        File.WriteAllBytes(Path.Combine(world, "cacheMinimapMeta"), [1]);

        var entry = await Service.CreateAsync(profile, BackupKind.Manual, "teste", TestContext.Current.CancellationToken);

        Assert.Equal(11, entry.SaveNumber);
        Assert.StartsWith("MeuMundo_", entry.Name);
        Assert.EndsWith("_save11_manual", entry.Name);
        Assert.NotNull(entry.Manifest);
        Assert.True(entry.Manifest.CreativeKey);
        Assert.Equal(38720, entry.Manifest.TotalZdos);
        Assert.Equal(8, entry.Manifest.Files.Count);
        Assert.DoesNotContain(entry.Manifest.Files, f => f.Name.StartsWith("_main.10", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(entry.Directory, "20_1e__1_9.chunk")));

        var verification = await Service.VerifyAsync(entry, TestContext.Current.CancellationToken);
        Assert.True(verification.Ok, string.Join(" ", verification.Problems));
    }

    [Fact]
    public async Task Verification_detects_tampering()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        TestWorlds.WriteSave(WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo"), "MeuMundo", 3, TestWorlds.DefaultChunks);
        var entry = await Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken);

        await File.WriteAllBytesAsync(Path.Combine(entry.Directory, "_main.3.db2"), [9, 9, 9], TestContext.Current.CancellationToken);

        var verification = await Service.VerifyAsync(entry, TestContext.Current.CancellationToken);
        Assert.False(verification.Ok);
        Assert.Contains(verification.Problems, p => p.Contains("_main.3.db2"));
    }

    [Fact]
    public async Task Refuses_to_back_up_a_broken_world()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        TestWorlds.WriteSave(WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo"), "MeuMundo", 17, TestWorlds.DefaultChunks, writeDb2: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(profile.EffectiveBackupDirectory) && Directory.EnumerateDirectories(profile.EffectiveBackupDirectory).Any());
    }

    [Fact]
    public async Task Restore_replaces_the_world_and_keeps_the_previous_one()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        var world = WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo");
        TestWorlds.WriteSave(world, "MeuMundo", 5, TestWorlds.DefaultChunks);
        var good = await Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken);

        // The world moves on, then breaks exactly like on 2026-09-16.
        TestWorlds.WriteSave(world, "MeuMundo", 6, [(0x1e, 0x20, 12, 99)], writeDb2: false);
        Assert.False(WorldInspector.Inspect(profile.SaveDirectory, "MeuMundo").IsHealthy);

        var result = await Service.RestoreAsync(profile, good, TestContext.Current.CancellationToken);

        var after = WorldInspector.Inspect(profile.SaveDirectory, "MeuMundo");
        Assert.True(after.IsHealthy);
        Assert.Equal(5, after.LatestSave!.Number);
        Assert.Single(WorldFolder.ScanSaveSets(world));
        Assert.NotNull(result.SafetyCopy);
        Assert.Equal(BackupKind.Quarantine, result.SafetyCopy.Kind);
        Assert.True(Directory.Exists(result.ReplacedFolder));
        Assert.True(File.Exists(Path.Combine(result.ReplacedFolder!, "_main.6.fwl2")));
        Assert.False(File.Exists(Path.Combine(world, BackupManifest.FileName)));
    }

    [Fact]
    public async Task Restore_of_a_healthy_world_takes_a_pre_restore_backup()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        var world = WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo");
        TestWorlds.WriteSave(world, "MeuMundo", 5, TestWorlds.DefaultChunks);
        var first = await Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken);
        Directory.Delete(world, true);
        TestWorlds.WriteSave(world, "MeuMundo", 9, [(0x20, 0x20, 1, 7)]);

        var result = await Service.RestoreAsync(profile, first, TestContext.Current.CancellationToken);

        Assert.Equal(BackupKind.PreRestore, result.SafetyCopy!.Kind);
        Assert.Equal(9, result.SafetyCopy.SaveNumber);
    }

    [Fact]
    public async Task Restore_refuses_a_backup_of_another_world()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        TestWorlds.WriteSave(WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo"), "MeuMundo", 5, TestWorlds.DefaultChunks);
        var entry = await Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken);
        profile.WorldName = "Sandbox";

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service.RestoreAsync(profile, entry, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Lists_manifest_backups_imported_folders_and_game_auto_backups()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        TestWorlds.WriteSave(WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo"), "MeuMundo", 5, TestWorlds.DefaultChunks);
        await Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken);
        TestWorlds.WriteSave(Path.Combine(profile.EffectiveBackupDirectory, "MeuMundo_20260916-061830_save33"), "MeuMundo", 33, TestWorlds.DefaultChunks);
        TestWorlds.WriteSave(WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo_backup_auto-20260915-213929"), "MeuMundo", 1, TestWorlds.DefaultChunks);
        Directory.CreateDirectory(Path.Combine(profile.EffectiveBackupDirectory, "pasta-qualquer"));

        var list = Service.List(profile);

        Assert.Equal(3, list.Count);
        var imported = Assert.Single(list, e => e.Kind == BackupKind.Imported);
        Assert.Equal(33, imported.SaveNumber);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 9, 16, 6, 18, 30)), imported.CreatedAt);
        Assert.Single(list, e => e.Kind == BackupKind.GameAuto);
        Assert.Single(list, e => e.Kind == BackupKind.Manual);
    }

    [Fact]
    public async Task Retention_only_removes_old_automatic_backups()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        profile.AutoBackupRetention = 2;
        var world = WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo");
        TestWorlds.WriteSave(world, "MeuMundo", 1, TestWorlds.DefaultChunks);
        await Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken);

        for (var n = 2; n <= 5; n++)
        {
            TestWorlds.WriteSave(world, "MeuMundo", n, TestWorlds.DefaultChunks);
            await Service.CreateAsync(profile, BackupKind.PostStop, ct: TestContext.Current.CancellationToken);
            await Task.Delay(1100, TestContext.Current.CancellationToken);
        }

        var list = Service.List(profile, includeGameAutoBackups: false);
        Assert.Single(list, e => e.Kind == BackupKind.Manual);
        Assert.Equal([5, 4], list.Where(e => e.Kind == BackupKind.PostStop).Select(e => e.SaveNumber));
    }

    [Fact]
    public async Task Delete_only_works_inside_the_backup_folder()
    {
        using var tmp = new TempDir();
        var profile = tmp.Profile();
        TestWorlds.WriteSave(WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo"), "MeuMundo", 1, TestWorlds.DefaultChunks);
        var entry = await Service.CreateAsync(profile, BackupKind.Manual, ct: TestContext.Current.CancellationToken);

        var outside = entry with { Directory = WorldFolder.WorldDirectory(profile.SaveDirectory, "MeuMundo") };
        Assert.Throws<InvalidOperationException>(() => Service.Delete(profile, outside));

        Service.Delete(profile, entry);
        Assert.False(Directory.Exists(entry.Directory));
    }
}
