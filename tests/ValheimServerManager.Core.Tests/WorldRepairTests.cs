using System.IO.Compression;
using System.Text;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Tests;

/// <summary>Builds real ZDO bytes the way <c>ZDO.Save</c> writes them.</summary>
internal static class Zdo
{
    public static readonly int Tree = StableHash.Compute("Beech1");
    public static readonly int Mushroom = StableHash.Compute("Pickable_Mushroom");
    public static readonly int Chest = StableHash.Compute("TreasureChest_forestcrypt");
    public static readonly int Stone = StableHash.Compute("Pickable_Stone");
    public static readonly int Proxy = StableHash.Compute("LocationProxy");
    public static readonly int ZoneCtrl = StableHash.Compute("_ZoneCtrl");

    /// <summary>Items as a container stores them (<c>Inventory.Save</c>, version 109).</summary>
    public static byte[] Inventory(params (int Prefab, int Stack, bool Cheated, string? Crafter)[] items)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(109);
        w.Write((ushort)items.Length);
        foreach (var (prefab, stack, cheated, crafter) in items)
        {
            var flags = 0x40 | (stack != 1 ? 8 : 0) | (crafter is not null ? 0x20 : 0);
            w.Write(10000);              // durability
            w.Write((byte)0);            // grid x
            w.Write((byte)0);            // grid y
            w.Write((byte)0);            // world level
            w.Write((byte)flags);
            if (stack != 1)
            {
                w.Write((ushort)stack);
            }

            if (crafter is not null)
            {
                w.Write(12345L);
                w.Write(crafter);
            }

            w.Write(prefab);
            w.Write((byte)(cheated ? 1 : 0));
        }

        w.Flush();
        return ms.ToArray();
    }

    public static byte[] Make(int prefab, float x, float y, float z, float? yaw = null, int[]? ints = null,
        string? text = null, byte[]? blob = null, bool connection = false, bool smallPosition = false, bool tilted = false,
        long? creator = null, bool cheated = false, byte[]? items = null, byte[]? itemData = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        ushort flags = 0x100;
        if (connection) flags |= 0x01;
        if (ints is not null || cheated) flags |= 0x10;
        if (creator is not null) flags |= 0x20;
        if (items is not null || itemData is not null) flags |= 0x80;
        if (text is not null) flags |= 0x40;
        if (blob is not null) flags |= 0x80;
        if (yaw is not null || tilted) flags |= 0x1000;
        if (smallPosition) flags |= 0x2000;
        w.Write(flags);
        if (smallPosition)
        {
            w.Write((short)x);
            w.Write((short)z);
        }
        else
        {
            w.Write(x);
            w.Write(y);
            w.Write(z);
        }

        w.Write(prefab);
        if (tilted)
        {
            uint packed = 20 | (180u << 10) | (40u << 20);
            w.Write((ushort)(packed >> 16));
            w.Write((ushort)packed);
        }
        else if (yaw is { } angle)
        {
            w.Write((short)(0x8000 | (ushort)(angle * 2)));
        }

        if (connection)
        {
            w.Write((byte)2);
            w.Write(12345);
        }

        if (ints is not null || cheated)
        {
            w.Write((byte)((ints?.Length ?? 0) + (cheated ? 1 : 0)));
            for (var i = 0; i < (ints?.Length ?? 0); i++)
            {
                w.Write(1000 + i);
                w.Write(ints![i]);
            }

            if (cheated)
            {
                w.Write(StableHash.Compute("cheated"));
                w.Write(1);
            }
        }

        if (creator is { } builder)
        {
            w.Write((byte)2);
            w.Write(55);
            w.Write(1L);
            w.Write(StableHash.Compute("creator"));
            w.Write(builder);
        }

        if (text is not null)
        {
            w.Write((byte)1);
            w.Write(77);
            w.Write(text);
        }

        if (blob is not null || items is not null || itemData is not null)
        {
            w.Write((byte)((blob is null ? 0 : 1) + (items is null ? 0 : 1) + (itemData is null ? 0 : 1)));
            if (blob is not null)
            {
                w.Write(88);
                w.Write(blob.Length);
                w.Write(blob);
            }

            if (items is not null)
            {
                w.Write(StableHash.Compute("items"));
                w.Write(items.Length);
                w.Write(items);
            }

            if (itemData is not null)
            {
                w.Write(StableHash.Compute("itemData"));
                w.Write(itemData.Length);
                w.Write(itemData);
            }
        }

        w.Flush();
        return ms.ToArray();
    }

    public static byte[] Chunk(IEnumerable<byte[]> zdos)
    {
        var list = zdos.ToList();
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)41);
        w.Write(list.Count);
        foreach (var z in list)
        {
            w.Write(z);
        }

        w.Flush();
        return ms.ToArray();
    }

    public static byte[] Db2(IEnumerable<(short X, short Z)> zones, byte[]? rest = null, byte[]? tail = null)
    {
        var zoneList = zones.ToList();
        using var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(zoneList.Count);
            foreach (var (x, z) in zoneList)
            {
                w.Write(x);
                w.Write(z);
            }

            w.Write(rest ?? [7, 0, 0, 0, 1, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c', 1, 0, 0, 0, 0]);
        }

        using var gz = new MemoryStream();
        using (var g = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
        {
            g.Write(payload.ToArray());
        }

        using var file = new MemoryStream();
        using var fw = new BinaryWriter(file);
        fw.Write(41);
        fw.Write(1234.5);
        fw.Write((int)gz.Length);
        fw.Write(gz.ToArray());
        fw.Write(tail ?? [9, 9, 9]);
        fw.Flush();
        return file.ToArray();
    }

    /// <summary>Writes a complete save N with one chunk file holding the given objects.</summary>
    public static string WriteWorld(string saveDirectory, int saveNumber, IEnumerable<byte[]> zdos, IEnumerable<(short, short)> generatedZones)
    {
        var dir = WorldFolder.WorldDirectory(saveDirectory, "Mundo");
        Directory.CreateDirectory(dir);
        var list = zdos.ToList();
        var entry = new ChunkIndexEntry(0x1e, 0x20, 1, 3, list.Count);
        File.WriteAllBytes(Path.Combine(dir, entry.FileName), Chunk(list));
        File.WriteAllBytes(Path.Combine(dir, $"_main.{saveNumber}.chunks"), new ChunkIndex(41, [entry]).ToBytes());
        File.WriteAllBytes(Path.Combine(dir, $"_main.{saveNumber}.db2"), Db2(generatedZones));
        File.WriteAllBytes(Path.Combine(dir, $"_main.{saveNumber}.fwl2"), TestWorlds.Fwl2Bytes("Mundo"));
        File.WriteAllBytes(Path.Combine(dir, $"_main.{saveNumber}.ok"), [0x29, 0, 0, 0]);
        return dir;
    }
}

public class WorldObjectFormatTests
{
    [Fact]
    public void Reads_every_kind_of_object_and_writes_it_back_unchanged()
    {
        byte[][] zdos =
        [
            Zdo.Make(Zdo.Tree, 10.5f, 30f, -20.25f),
            Zdo.Make(Zdo.Mushroom, -700, 0, 300, smallPosition: true),
            Zdo.Make(Zdo.Proxy, 1, 2, 3, yaw: 247.5f, ints: [5, -6]),
            Zdo.Make(Zdo.Chest, 4, 5, 6, tilted: true, text: "itens çãõ", blob: [1, 2, 3, 4, 5], connection: true),
        ];
        var bytes = Zdo.Chunk(zdos);

        var file = ChunkObjects.Parse(bytes);

        Assert.Equal(41, file.Version);
        Assert.Equal([Zdo.Tree, Zdo.Mushroom, Zdo.Proxy, Zdo.Chest], file.Objects.Select(o => o.Prefab));
        Assert.Equal((10.5f, 30f, -20.25f), (file.Objects[0].X, file.Objects[0].Y, file.Objects[0].Z));
        Assert.Equal((-700f, 0f, 300f), (file.Objects[1].X, file.Objects[1].Y, file.Objects[1].Z));
        Assert.Equal(247.5f, file.Objects[2].Yaw);
        Assert.Null(file.Objects[3].Yaw);
        Assert.Equal(zdos.Select(z => z.Length), file.Objects.Select(o => o.Length));
        Assert.Equal(bytes, file.ToBytes(file.Objects.ToArray()));
    }

    [Fact]
    public void Zone_matches_the_game()
    {
        Assert.Equal(((short)0, (short)0), new WorldObject(0, 31.9f, 0, -32f, 0, 0, 0, 0).Zone);
        Assert.Equal(((short)-1, (short)1), new WorldObject(0, -32.1f, 0, 32f, 0, 0, 0, 0).Zone);
    }

    [Fact]
    public void Rejects_truncated_chunks()
    {
        var bytes = Zdo.Chunk([Zdo.Make(Zdo.Tree, 1, 2, 3, text: "abc")]);
        Assert.Throws<InvalidDataException>(() => ChunkObjects.Parse(bytes[..^2]));
        Assert.Throws<InvalidDataException>(() => ChunkObjects.Parse([.. bytes, 0]));
    }

    [Fact]
    public void Database_keeps_everything_but_the_zone_list()
    {
        var original = Zdo.Db2([(1, 2), (-3, 4)], tail: [4, 3, 2, 1]);

        var db = WorldDatabase.Parse(original);
        Assert.Equal([((short)1, (short)2), ((short)-3, (short)4)], db.GeneratedZones);

        db.GeneratedZones.Add((-5, -6));
        var again = WorldDatabase.Parse(db.ToBytes());

        Assert.Equal([((short)1, (short)2), ((short)-3, (short)4), ((short)-5, (short)-6)], again.GeneratedZones);
        Assert.Equal(original[..12], again.ToBytes()[..12]);
        Assert.Equal([4, 3, 2, 1], again.ToBytes()[^4..]);
        Assert.Throws<InvalidDataException>(() => WorldDatabase.Parse(new byte[512]));
    }
}

public class WorldRepairTests
{
    [Fact]
    public void Scan_finds_copies_double_spawn_and_zones_the_game_would_generate_again()
    {
        using var temp = new TempDir();
        Zdo.WriteWorld(temp.Path, 5,
        [
            Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0),
            Zdo.Make(Zdo.ZoneCtrl, 64, 0, 0),
            Zdo.Make(Zdo.Mushroom, 3, 1, 3),
            Zdo.Make(Zdo.Mushroom, 3, 1, 3),
            Zdo.Make(Zdo.ZoneCtrl, 64, 0, 0),
            Zdo.Make(Zdo.Tree, 70, 1, 5, yaw: 90),
            Zdo.Make(Zdo.Tree, 70, 1, 5, yaw: 180),
        ], [(0, 0)]);

        var scan = WorldRepair.Scan(temp.Path, "Mundo");

        Assert.Equal(7, scan.Objects);
        Assert.Equal(2, scan.ExtraCopies);
        Assert.Equal(1, scan.ZonesWithDoubleSpawn);
        Assert.Equal(1, scan.ZonesToMark);
        Assert.Contains(("cogumelos", 1), scan.ByCategory);
        Assert.Contains(("controles de spawn de zona", 1), scan.ByCategory);
        Assert.True(scan.NeedsRepair);
    }

    [Fact]
    public void Repair_writes_the_next_save_keeps_originals_and_marks_zones()
    {
        using var temp = new TempDir();
        var original = Zdo.Make(Zdo.Chest, 3, 1, 3, text: "saqueado");
        var fresh = Zdo.Make(Zdo.Chest, 3, 1, 3, text: "cheio de ouro");
        var dir = Zdo.WriteWorld(temp.Path, 5,
        [
            Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0),
            original,
            Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0),
            fresh,
            Zdo.Make(Zdo.Tree, -64, 1, 64),
            Zdo.Make(Zdo.ZoneCtrl, -64, 0, 64),
        ], []);
        var before = Directory.GetFiles(dir).ToDictionary(f => f, File.ReadAllBytes);

        var result = WorldRepair.Repair(temp.Path, "Mundo");

        Assert.Equal((5, 6, 2, 2, 4L), (result.OldSaveNumber, result.NewSaveNumber, result.RemovedObjects, result.ZonesMarked, result.ObjectsAfter));
        foreach (var (path, bytes) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }

        var report = WorldInspector.Inspect(temp.Path, "Mundo");
        Assert.True(report.IsHealthy);
        Assert.Equal(6, report.LatestSave!.Number);
        var entry = Assert.Single(report.Index!.Entries);
        Assert.Equal(4, entry.Revision);
        var chunk = File.ReadAllBytes(Path.Combine(dir, entry.FileName));
        Assert.Equal(1, CountOf(chunk, original));
        Assert.Equal(0, CountOf(chunk, fresh));

        var zones = WorldDatabase.Read(Path.Combine(dir, "_main.6.db2")).GeneratedZones;
        Assert.Equal([((short)-1, (short)1), ((short)0, (short)0)], zones.Order());
        Assert.False(WorldRepair.Scan(temp.Path, "Mundo").NeedsRepair);
        Assert.Equal(6, WorldRepair.Repair(temp.Path, "Mundo").NewSaveNumber);
    }

    [Fact]
    public void Repair_removes_a_location_placed_again_with_another_rotation_and_its_contents()
    {
        using var temp = new TempDir();
        const float cx = 100, cz = 200;
        Zdo.WriteWorld(temp.Path, 1,
        [
            Zdo.Make(Zdo.ZoneCtrl, 96, 0, 192),
            Zdo.Make(Zdo.Proxy, cx, 10, cz, yaw: 0, ints: [42, 7]),
            Zdo.Make(Zdo.Chest, cx + 10, 10, cz),          // original content
            Zdo.Make(Zdo.Stone, cx, 10, cz + 5),           // A: symmetric original layout
            Zdo.Make(Zdo.Stone, cx - 5, 10, cz),           // B = A rotated
            Zdo.Make(Zdo.Proxy, cx, 10, cz, yaw: 90, ints: [42, 7]),
            Zdo.Make(Zdo.Chest, cx, 10, cz - 10),          // copy of the chest, rotated
            Zdo.Make(Zdo.Stone, cx + 5, 10, cz),           // copy of A, rotated
            Zdo.Make(Zdo.Stone, cx, 10, cz + 5),           // copy of B lands exactly on A
            Zdo.Make(Zdo.Proxy, cx, 10, cz, yaw: 90, ints: [43, 7]), // another location on the same spot
            Zdo.Make(Zdo.Tree, cx + 20, 10, cz + 20),      // unrelated
        ], [(2, 3)]);

        var scan = WorldRepair.Scan(temp.Path, "Mundo");
        Assert.Equal(4, scan.ExtraCopies);
        Assert.Contains(("entradas e estruturas", 1), scan.ByCategory);
        Assert.Contains(("conteúdo de ruínas e locais repetidos", 2), scan.ByCategory);

        var result = WorldRepair.Repair(temp.Path, "Mundo");
        Assert.Equal(7, result.ObjectsAfter);
        var report = WorldInspector.Inspect(temp.Path, "Mundo");
        var objects = ChunkObjects.Read(Path.Combine(report.Directory, report.Index!.Entries[0].FileName)).Objects;
        Assert.Contains(objects, o => o.Prefab == Zdo.Chest && o.X == cx + 10);
        Assert.DoesNotContain(objects, o => o.Prefab == Zdo.Chest && o.Z == cz - 10);
        Assert.Contains(objects, o => o.Prefab == Zdo.Stone && o.Z == cz + 5);
        Assert.Contains(objects, o => o.Prefab == Zdo.Stone && o.X == cx - 5);
        Assert.DoesNotContain(objects, o => o.Prefab == Zdo.Stone && o.X == cx + 5);
        Assert.Equal(2, objects.Count(o => o.Prefab == Zdo.Proxy));
    }

    [Fact]
    public void Never_touches_what_players_built()
    {
        using var temp = new TempDir();
        var chest = StableHash.Compute("piece_chest_wood");
        var wall = StableHash.Compute("woodwall");
        Zdo.WriteWorld(temp.Path, 2,
        [
            Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0),
            Zdo.Make(chest, 5, 1, 5, yaw: 90, text: "espada", creator: 42),
            Zdo.Make(chest, 5, 1, 5, yaw: 90, text: "escudo", creator: 42),
            Zdo.Make(StableHash.Compute("Player_tombstone"), 6, 1, 6),
            Zdo.Make(StableHash.Compute("Player_tombstone"), 6, 1, 6),
            Zdo.Make(wall, 9, 1, 9, yaw: 90),                 // part of a generated ruin…
            Zdo.Make(wall, 9, 1, 9, yaw: 90),                 // …placed twice
        ], [(0, 0)]);

        var scan = WorldRepair.Scan(temp.Path, "Mundo");
        Assert.Equal(1, scan.ExtraCopies);
    }

    [Fact]
    public void Leaves_a_clean_world_alone()
    {
        using var temp = new TempDir();
        Zdo.WriteWorld(temp.Path, 3, [Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0), Zdo.Make(Zdo.Tree, 1, 1, 1)], [(0, 0)]);

        var result = WorldRepair.Repair(temp.Path, "Mundo");

        Assert.Equal((3, 3, 0), (result.OldSaveNumber, result.NewSaveNumber, result.RemovedObjects));
        Assert.Equal(4, Directory.GetFiles(WorldFolder.WorldDirectory(temp.Path, "Mundo"), "_main.*").Length);
    }

    [Fact]
    public void Refuses_a_broken_world_without_touching_it()
    {
        using var temp = new TempDir();
        var dir = Zdo.WriteWorld(temp.Path, 3, [Zdo.Make(Zdo.Tree, 1, 1, 1)], []);
        File.Delete(Path.Combine(dir, "_main.3.db2"));

        Assert.Throws<InvalidOperationException>(() => WorldRepair.Repair(temp.Path, "Mundo"));
        Assert.Equal(3, Directory.GetFiles(dir, "_main.*").Length);
    }

    private static int CountOf(byte[] haystack, byte[] needle)
    {
        var count = 0;
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                count++;
            }
        }

        return count;
    }
}

public class WorldCheatMarkTests
{
    private static readonly int Wall = StableHash.Compute("woodwall");
    private static readonly int Creature = StableHash.Compute("Greydwarf");

    /// <summary>A dropped item is stored as "u8 version | item", an inventory as "i32 version | u16 count | items".</summary>
    private static byte[] DroppedItem(int prefab, bool cheated) =>
        [109, .. Zdo.Inventory((prefab, 3, cheated, "Bjorn"))[6..]];

    private static byte[] Chest() => Zdo.Inventory(
        (Zdo.Mushroom, 11, true, null), (Zdo.Mushroom, 1, false, null), (Zdo.Stone, 20, true, "Bjorn"));

    [Fact]
    public void Scan_counts_marked_pieces_creatures_and_items()
    {
        using var temp = new TempDir();
        Zdo.WriteWorld(temp.Path, 4,
        [
            Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0),
            Zdo.Make(Wall, 1, 1, 1, creator: 7, cheated: true),
            Zdo.Make(Wall, 2, 1, 1, creator: 7),
            Zdo.Make(Creature, 3, 1, 1, cheated: true),
            Zdo.Make(Zdo.Chest, 4, 1, 1, creator: 7, items: Chest()),
            Zdo.Make(Zdo.Tree, 5, 1, 1, itemData: DroppedItem(Zdo.Stone, cheated: true)),
        ], [(0, 0)]);

        var scan = WorldCheatMarks.Scan(temp.Path, "Mundo");

        Assert.Equal((1, 1, 3), (scan.MarkedPieces, scan.MarkedOthers, scan.MarkedItems));
        Assert.True(scan.NeedsCleaning);
    }

    [Fact]
    public void Clean_clears_every_mark_and_changes_nothing_else()
    {
        using var temp = new TempDir();
        var dir = Zdo.WriteWorld(temp.Path, 4,
        [
            Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0),
            Zdo.Make(Wall, 1, 1, 1, creator: 7, cheated: true),
            Zdo.Make(Zdo.Chest, 4, 1, 1, creator: 7, items: Chest()),
            Zdo.Make(Zdo.Tree, 5, 1, 1, itemData: DroppedItem(Zdo.Stone, cheated: true)),
        ], [(0, 0)]);
        var chunk = Path.Combine(dir, WorldInspector.Inspect(temp.Path, "Mundo").Index!.Entries[0].FileName);
        var before = File.ReadAllBytes(chunk);

        var result = WorldCheatMarks.Clean(temp.Path, "Mundo");

        Assert.Equal((4, 5, 1, 3), (result.OldSaveNumber, result.NewSaveNumber, result.ClearedObjects, result.ClearedItems));
        var report = WorldInspector.Inspect(temp.Path, "Mundo");
        var after = File.ReadAllBytes(Path.Combine(dir, report.Index!.Entries[0].FileName));
        Assert.Equal(before.Length, after.Length);
        // One byte of the piece's int plus one flag byte per item, and every change only clears bit 0.
        Assert.Equal(4, before.Zip(after).Count(p => p.First != p.Second));
        Assert.Equal(4, CountClearedBits(before, after));
        Assert.Equal(before, File.ReadAllBytes(chunk));                       // save 4 untouched
        Assert.False(WorldCheatMarks.Scan(temp.Path, "Mundo").NeedsCleaning);
        Assert.Equal(5, WorldCheatMarks.Clean(temp.Path, "Mundo").NewSaveNumber);
    }

    [Fact]
    public void Leaves_a_clean_world_and_its_files_alone()
    {
        using var temp = new TempDir();
        var dir = Zdo.WriteWorld(temp.Path, 2, [Zdo.Make(Zdo.ZoneCtrl, 0, 0, 0), Zdo.Make(Zdo.Tree, 1, 1, 1)], [(0, 0)]);

        var result = WorldCheatMarks.Clean(temp.Path, "Mundo");

        Assert.Equal((2, 2, 0, 0), (result.OldSaveNumber, result.NewSaveNumber, result.ClearedObjects, result.ClearedItems));
        Assert.Equal(4, Directory.GetFiles(dir, "_main.*").Length);
    }

    private static int CountClearedBits(byte[] before, byte[] after)
    {
        var bits = 0;
        for (var i = 0; i < before.Length; i++)
        {
            if ((before[i] ^ after[i]) == 1 && (before[i] & 1) == 1)
            {
                bits++;
            }
        }

        return bits;
    }
}
