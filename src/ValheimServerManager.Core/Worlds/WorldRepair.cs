using System.Globalization;
using ValheimServerManager.Core.Localization;

namespace ValheimServerManager.Core.Worlds;

/// <summary>What a duplicate scan found, grouped for people.</summary>
public sealed record WorldDuplicateReport(
    int SaveNumber,
    long Objects,
    int ExtraCopies,
    int ZonesWithDoubleSpawn,
    int ZonesToMark,
    IReadOnlyList<(string Category, int Count)> ByCategory)
{
    public bool NeedsRepair => ExtraCopies > 0 || ZonesToMark > 0;

    /// <summary>One line for alerts, e.g. "2,681 pickables, 135 mushrooms…".</summary>
    public string Summary =>
        string.Join(", ", ByCategory.Take(5).Select(c => c.Count.ToString("N0", CultureInfo.CurrentCulture) + " " + c.Category));
}

public sealed record WorldRepairResult(int OldSaveNumber, int NewSaveNumber, int RemovedObjects, int ZonesMarked, long ObjectsAfter);

/// <summary>
/// Finds and removes world objects that exist twice, and marks every zone that already has objects
/// as generated.
/// </summary>
/// <remarks>
/// Valheim keeps the list of generated zones in <c>_main.N.db2</c>. A zone missing from that list is
/// generated again when a player comes near: vegetation, ore, pickables, dungeon entrances and a
/// second <c>_ZoneCtrl</c> (which doubles enemy spawns) are placed on top of what is there. This
/// happens when chunks of one save are paired with the <c>.db2</c> of another, as in the
/// 2026-09-16 recovery. Copies placed by the game are bit-identical to the original (same prefab,
/// position and rotation); the first one in the file is the original and is kept.
/// </remarks>
public static class WorldRepair
{
    public static readonly int ZoneControlPrefab = StableHash.Compute("_ZoneCtrl");

    private const double LocationRadius = 40;
    private const double Tolerance = 0.05;
    private static readonly int LocationProxyPrefab = StableHash.Compute("LocationProxy");

    private static readonly (string Category, string[] Prefabs)[] Categories =
    [
        (Strings.Repair_CategoryMushrooms, ["Pickable_Mushroom", "Pickable_Mushroom_yellow", "Pickable_Mushroom_blue"]),
        (Strings.Repair_CategoryCopperTin, ["rock4_copper", "rock4_copper_frac", "MineRock_Tin"]),
        (Strings.Repair_CategorySpawners, ["Spawner_Skeleton", "Spawner_GreydwarfNest", "Spawner_Greydwarf", "Spawner_Ghost",
            "Spawner_DraugrPile", "Spawner_Draugr", "BonePileSpawner", "Spawner_Blob", "Spawner_Wolf"]),
        (Strings.Repair_CategoryZoneControls, ["_ZoneCtrl"]),
        (Strings.Repair_CategoryTreasureChests, ["TreasureChest_forestcrypt", "TreasureChest_blackforest", "TreasureChest_meadows",
            "TreasureChest_trollcave", "TreasureChest_mountains", "TreasureChest_swamp"]),
        (Strings.Repair_CategoryEntrances, ["LocationProxy", "dungeon_forestcrypt_door"]),
        (Strings.Repair_CategoryPickables, ["Pickable_Stone", "Pickable_Branch", "Pickable_Flint",
            "Pickable_Dandelion", "Pickable_Thistle", "RaspberryBush", "BlueberryBush", "CloudberryBush"]),
        (Strings.Repair_CategoryTrees, ["Beech1", "Beech_small1", "Beech_small2", "FirTree", "FirTree_small", "Pinetree_01",
            "Birch1", "Birch2", "Oak1", "FirTree_oldLog", "stubbe", "SwampTree1"]),
        (Strings.Repair_CategoryRocks, ["Rock_3", "Rock_4", "rock4_coast", "rock4_forest", "rock4_forest_frac", "rock1_mountain"]),
        (Strings.Repair_CategoryBushes, ["shrub_2", "shrub_2_heath", "Bush01", "Bush02_en", "Bush01_heath"]),
    ];

    private static readonly int Tombstone = StableHash.Compute("Player_tombstone");

    private static readonly Dictionary<int, string> CategoryByPrefab = Categories
        .SelectMany(c => c.Prefabs.Select(p => (Hash: StableHash.Compute(p), c.Category)))
        .ToDictionary(x => x.Hash, x => x.Category);

    public static WorldDuplicateReport Scan(string saveDirectory, string worldName) =>
        ScanDirectory(WorldFolder.WorldDirectory(saveDirectory, worldName), worldName);

    public static WorldDuplicateReport ScanDirectory(string worldDirectory, string? worldName = null) =>
        Load(worldDirectory, worldName).Report;

    /// <summary>
    /// Writes save N+1 without the extra copies and with every populated zone marked as generated.
    /// The server must be stopped; take a backup first.
    /// </summary>
    public static WorldRepairResult Repair(string saveDirectory, string worldName) =>
        RepairDirectory(WorldFolder.WorldDirectory(saveDirectory, worldName), worldName);

    public static WorldRepairResult RepairDirectory(string worldDirectory, string? worldName = null)
    {
        var world = Load(worldDirectory, worldName);
        if (!world.Report.NeedsRepair)
        {
            return new WorldRepairResult(world.Save.Number, world.Save.Number, 0, 0, world.Report.Objects);
        }

        var dir = worldDirectory;
        var next = WorldFolder.ScanSaveSets(dir).Max(s => s.Number) + 1;
        var written = new List<string>();
        try
        {
            var entries = new List<ChunkIndexEntry>();
            foreach (var chunk in world.Chunks)
            {
                if (chunk.Keep.Count == chunk.File.Objects.Count)
                {
                    entries.Add(chunk.Entry);
                    continue;
                }

                var revision = NextRevision(dir, chunk.Entry);
                var entry = chunk.Entry with { Revision = revision, ZdoCount = chunk.Keep.Count };
                written.Add(WriteNew(Path.Combine(dir, entry.FileName), chunk.File.ToBytes(chunk.Keep)));
                entries.Add(entry);
            }

            var db = world.Database;
            foreach (var zone in world.PopulatedZones.Except(db.GeneratedZones).ToArray())
            {
                db.GeneratedZones.Add(zone);
            }

            var index = new ChunkIndex(world.Index.Version, entries);
            written.Add(WriteNew(Path.Combine(dir, $"_main.{next}.db2"), db.ToBytes()));
            written.Add(WriteNew(Path.Combine(dir, $"_main.{next}.fwl2"), File.ReadAllBytes(world.Save.Fwl2!)));
            written.Add(WriteNew(Path.Combine(dir, $"_main.{next}.chunks"), index.ToBytes()));

            written.Add(WriteNew(Path.Combine(dir, $"_main.{next}.ok"), File.ReadAllBytes(world.Save.Ok!)));

            var after = Load(worldDirectory, worldName);
            if (after.Save.Number != next || after.Report.ExtraCopies != 0 || after.Report.ZonesToMark != 0 ||
                after.Report.Objects != world.Report.Objects - world.Report.ExtraCopies)
            {
                throw new InvalidDataException(Strings.Repair_VerificationFailed);
            }

            return new WorldRepairResult(world.Save.Number, next, world.Report.ExtraCopies, world.Report.ZonesToMark, after.Report.Objects);
        }
        catch
        {
            // Nothing of save N was touched; drop the partial N+1 so the world stays as it was.
            foreach (var path in Enumerable.Reverse(written))
            {
                TryDelete(path);
            }

            throw;
        }
    }

    private static LoadedWorld Load(string worldDirectory, string? worldName)
    {
        var report = WorldInspector.InspectDirectory(worldDirectory, worldName);
        if (!report.IsHealthy || report.LatestSave is not { } save || report.Index is not { } index)
        {
            throw new InvalidOperationException(Strings.Repair_WorldMustBeIntact + " " +
                string.Join(" ", report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
        }

        var chunks = index.Entries
            .Select(e => new LoadedChunk(e, ChunkObjects.Read(Path.Combine(report.Directory, e.FileName)), []))
            .ToList();
        var all = chunks.SelectMany((c, ci) => c.File.Objects.Select((o, oi) => new Ref(ci, oi, o))).ToList();
        var removed = new HashSet<(int Chunk, int Object)>();
        var extras = new Dictionary<string, int>(StringComparer.Ordinal);

        // 1. Copies placed by the game: same prefab, position and rotation (and, for locations, the
        //    same location). The first one is the original; its data may differ (a looted chest).
        var seen = new HashSet<(int, float, float, float, uint, string?)>();
        foreach (var r in all)
        {
            if (IsPlayerMade(r.Obj))
            {
                continue;
            }

            var identity = r.Obj.Prefab == LocationProxyPrefab ? LocationIdentity(chunks, r) : null;
            if (!seen.Add((r.Obj.Prefab, r.Obj.X, r.Obj.Y, r.Obj.Z, r.Obj.Rotation, identity)))
            {
                removed.Add((r.Chunk, r.Index));
                Count(extras, CategoryByPrefab.GetValueOrDefault(r.Obj.Prefab, Strings.Repair_CategoryOtherNature));
            }
        }

        // 2. Locations placed again with another rotation, and what they spawned around them.
        var kept = all.Where(r => !removed.Contains((r.Chunk, r.Index))).ToList();

        foreach (var r in RotatedLocationCopies(chunks, kept))
        {
            if (removed.Add((r.Chunk, r.Index)))
            {
                Count(extras, r.Obj.Prefab == LocationProxyPrefab ? Strings.Repair_CategoryEntrances : Strings.Repair_CategoryLocationContent);
            }
        }

        // 3. Objects on the very same spot with the same data but another rotation: one of them is
        //    invisible under the other and has to be chopped or mined a second time.
        var stacked = new Dictionary<(int, float, float, float, string), int>();
        foreach (var r in all)
        {
            if (removed.Contains((r.Chunk, r.Index)) || IsPlayerMade(r.Obj))
            {
                continue;
            }

            var key = (r.Obj.Prefab, r.Obj.X, r.Obj.Y, r.Obj.Z, Convert.ToHexString(chunks[r.Chunk].File.ExtraData(r.Obj)));
            if (stacked.TryGetValue(key, out var first) && first != r.Index)
            {
                removed.Add((r.Chunk, r.Index));
                Count(extras, Strings.Repair_CategoryStacked);
            }
            else
            {
                stacked[key] = r.Index;
            }
        }

        var controls = new HashSet<(short, short)>();
        foreach (var r in all)
        {
            if (removed.Contains((r.Chunk, r.Index)))
            {
                continue;
            }

            chunks[r.Chunk].Keep.Add(r.Obj);
            if (r.Obj.Prefab == ZoneControlPrefab)
            {
                controls.Add(r.Obj.Zone);
            }
        }

        // A zone with two controls spawns twice as much.
        var doubled = all.Where(r => r.Obj.Prefab == ZoneControlPrefab).GroupBy(r => r.Obj.Zone).Count(g => g.Count() > 1);

        var database = WorldDatabase.Read(save.Db2!);
        var marked = database.GeneratedZones.ToHashSet();
        var populated = controls.ToArray();
        var summary = new WorldDuplicateReport(
            save.Number,
            all.Count,
            removed.Count,
            doubled,
            populated.Count(z => !marked.Contains(z)),
            extras.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToArray());
        return new LoadedWorld(save, index, chunks, database, populated, summary);
    }

    /// <summary>
    /// A location generated twice lands on the same spot with the same data but usually another
    /// rotation, so its contents (chests, spawners, doors…) are the original contents rotated by the
    /// difference. An object is a copy when rotating it back lands on an object of the same prefab
    /// and nothing rotates onto it (which would make it part of a symmetric original layout).
    /// </summary>
    private static IEnumerable<Ref> RotatedLocationCopies(List<LoadedChunk> chunks, List<Ref> kept)
    {
        var proxies = kept.Where(r => r.Obj.Prefab == LocationProxyPrefab)
            .GroupBy(r => (r.Obj.X, r.Obj.Y, r.Obj.Z, LocationIdentity(chunks, r)))
            .Where(g => g.Count() > 1)
            .ToList();

        var grid = kept.ToLookup(r => Cell(r.Obj.X, r.Obj.Z));
        foreach (var group in proxies)
        {
            var original = group.First();
            foreach (var copy in group.Skip(1))
            {
                yield return copy;
                if (original.Obj.Yaw is not { } keepYaw || copy.Obj.Yaw is not { } dropYaw || keepYaw == dropYaw)
                {
                    continue;
                }

                var angle = (keepYaw - dropYaw) * Math.PI / 180;
                var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
                var edges = new List<(Ref From, Ref To)>();
                foreach (var r in Near(grid, original.Obj.X, original.Obj.Z, LocationRadius))
                {
                    if (r.Obj.Prefab == LocationProxyPrefab || IsPlayerMade(r.Obj))
                    {
                        continue;
                    }

                    double dx = r.Obj.X - original.Obj.X, dz = r.Obj.Z - original.Obj.Z;
                    var x = original.Obj.X + (dx * cos) + (dz * sin);
                    var z = original.Obj.Z - (dx * sin) + (dz * cos);
                    if (Math.Abs(x - r.Obj.X) < Tolerance && Math.Abs(z - r.Obj.Z) < Tolerance)
                    {
                        continue;
                    }

                    var target = r;
                    var match = Near(grid, x, z, Tolerance * 2).FirstOrDefault(m =>
                        m.Obj.Prefab == target.Obj.Prefab && m != target &&
                        Math.Abs(m.Obj.X - x) < Tolerance && Math.Abs(m.Obj.Z - z) < Tolerance &&
                        Math.Abs(m.Obj.Y - target.Obj.Y) < 0.5);
                    if (match is not null)
                    {
                        edges.Add((r, match));
                    }
                }

                var targets = edges.Select(e => e.To).ToHashSet();
                foreach (var (from, _) in edges.Where(e => !targets.Contains(e.From)))
                {
                    yield return from;
                }
            }
        }
    }

    /// <summary>World generation never places these, so the repair never touches them.</summary>
    private static bool IsPlayerMade(WorldObject obj) => obj.HasCreator || obj.Prefab == Tombstone;

    private static string LocationIdentity(List<LoadedChunk> chunks, Ref r) =>
        Convert.ToHexString(chunks[r.Chunk].File.ExtraData(r.Obj));

    private static (int, int) Cell(double x, double z) => ((int)Math.Floor(x / 8), (int)Math.Floor(z / 8));

    private static IEnumerable<Ref> Near(ILookup<(int, int), Ref> grid, double x, double z, double radius)
    {
        var (x0, z0) = Cell(x - radius, z - radius);
        var (x1, z1) = Cell(x + radius, z + radius);
        for (var gx = x0; gx <= x1; gx++)
        {
            for (var gz = z0; gz <= z1; gz++)
            {
                foreach (var r in grid[(gx, gz)])
                {
                    var (dx, dz) = (r.Obj.X - x, r.Obj.Z - z);
                    if ((dx * dx) + (dz * dz) <= radius * radius)
                    {
                        yield return r;
                    }
                }
            }
        }
    }

    private static void Count(Dictionary<string, int> counts, string category) =>
        counts[category] = counts.GetValueOrDefault(category) + 1;

    private static int NextRevision(string dir, ChunkIndexEntry entry)
    {
        var max = entry.Revision;
        foreach (var path in Directory.EnumerateFiles(dir, "*" + ChunkFileName.Extension))
        {
            if (ChunkFileName.TryParse(path, out var x, out var z, out var flag, out var revision) &&
                x == entry.X && z == entry.Z && flag == entry.Flag)
            {
                max = Math.Max(max, revision);
            }
        }

        return max + 1;
    }

    private static string WriteNew(string path, byte[] data)
    {
        if (File.Exists(path))
        {
            throw new IOException(string.Format(CultureInfo.CurrentCulture, Strings.World_FileAlreadyExists, Path.GetFileName(path)));
        }

        var temp = path + ".reparo";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Ref(int Chunk, int Index, WorldObject Obj);

    private sealed record LoadedChunk(ChunkIndexEntry Entry, ChunkObjects File, List<WorldObject> Keep);

    private sealed record LoadedWorld(
        SaveSet Save,
        ChunkIndex Index,
        List<LoadedChunk> Chunks,
        WorldDatabase Database,
        IReadOnlyList<(short X, short Z)> PopulatedZones,
        WorldDuplicateReport Report);
}
