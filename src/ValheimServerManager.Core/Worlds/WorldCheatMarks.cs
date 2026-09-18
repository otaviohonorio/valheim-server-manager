using System.Buffers.Binary;

namespace ValheimServerManager.Core.Worlds;

/// <summary>How many objects and items carry the game's "made with cheats" mark.</summary>
public sealed record CheatMarkReport(int SaveNumber, int MarkedPieces, int MarkedOthers, int MarkedItems)
{
    public int Total => MarkedPieces + MarkedOthers + MarkedItems;

    public bool NeedsCleaning => Total > 0;
}

public sealed record CheatMarkCleanResult(int OldSaveNumber, int NewSaveNumber, int ClearedObjects, int ClearedItems);

/// <summary>
/// Clears Valheim's "cheated" mark from a world.
/// </summary>
/// <remarks>
/// The game marks a piece placed with the console's no-cost cheat (<c>ZDO "cheated"</c>), a creature
/// hit by a cheating player, and every item that comes out of them: dismantling a marked piece
/// returns marked materials, and crafting with marked materials marks the result. Marked items never
/// stack with identical unmarked ones (<c>Inventory.FindFreeStackItem</c> compares the flag), which
/// is what players notice. Nothing else in the game depends on the mark except achievements, which
/// the game itself lets players re-enable with
/// <c>yesiuseddevcommandsbutiwantmyachievementsanyway 1</c> — that command only affects the player
/// who runs it and only new items, so a shared world has to be cleaned here.
/// The mark is one int value in the object and one bit per stored item, so everything is patched in
/// place and every other byte of the save is preserved.
/// </remarks>
public static class WorldCheatMarks
{
    public static CheatMarkReport Scan(string saveDirectory, string worldName) =>
        ScanDirectory(WorldFolder.WorldDirectory(saveDirectory, worldName), worldName);

    public static CheatMarkReport ScanDirectory(string worldDirectory, string? worldName = null)
    {
        var (save, index, chunks) = Load(worldDirectory, worldName);
        int pieces = 0, others = 0, items = 0;
        foreach (var (_, file, _) in chunks)
        {
            foreach (var obj in file.Objects)
            {
                if (IsMarked(file, obj))
                {
                    if (obj.HasCreator)
                    {
                        pieces++;
                    }
                    else
                    {
                        others++;
                    }
                }

                items += MarkedItemFlags(file, obj).Count;
            }
        }

        _ = index;
        return new CheatMarkReport(save.Number, pieces, others, items);
    }

    /// <summary>Writes save N+1 with every mark cleared. The server must be stopped; back up first.</summary>
    public static CheatMarkCleanResult Clean(string saveDirectory, string worldName) =>
        CleanDirectory(WorldFolder.WorldDirectory(saveDirectory, worldName), worldName);

    public static CheatMarkCleanResult CleanDirectory(string worldDirectory, string? worldName = null)
    {
        var (save, index, chunks) = Load(worldDirectory, worldName);
        var changed = new Dictionary<ChunkIndexEntry, byte[]>();
        int objects = 0, items = 0;
        foreach (var (entry, file, _) in chunks)
        {
            var buffer = file.CopyBytes();
            var touched = false;
            foreach (var obj in file.Objects)
            {
                if (IsMarked(file, obj))
                {
                    objects++;
                    touched = true;
                    foreach (var at in obj.CheatFlagOffsets)
                    {
                        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(at), 0);
                    }
                }

                foreach (var at in MarkedItemFlags(file, obj))
                {
                    items++;
                    touched = true;
                    buffer[at] &= 0xFE;
                }
            }

            if (touched)
            {
                changed[entry] = buffer;
            }
        }

        if (changed.Count == 0)
        {
            return new CheatMarkCleanResult(save.Number, save.Number, 0, 0);
        }

        var next = WorldSaveWriter.WriteNextSave(worldDirectory, save, index, changed, database: null);
        var after = ScanDirectory(worldDirectory, worldName);
        if (after.SaveNumber != next || after.NeedsCleaning)
        {
            throw new InvalidDataException("A conferência do mundo limpo falhou.");
        }

        return new CheatMarkCleanResult(save.Number, next, objects, items);
    }

    private static bool IsMarked(ChunkObjects file, WorldObject obj)
    {
        foreach (var at in obj.CheatFlagOffsets)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(file.Bytes(at, 4)) != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Offsets of the per-item flag bytes that have the cheated bit set, inside a container's
    /// <c>items</c> package or a dropped item's <c>itemData</c> (<c>Inventory.Save</c> /
    /// <c>ItemDrop.ItemData.Save</c>, item version 109).
    /// </summary>
    private static List<int> MarkedItemFlags(ChunkObjects file, WorldObject obj)
    {
        var found = new List<int>();
        if (obj.Inventory is not { } inventory || inventory.Length < 5)
        {
            return found;
        }

        // A container writes "i32 version | u16 count | items"; a dropped item writes "u8 version | item".
        var data = file.Bytes(inventory.Offset, inventory.Length);
        var o = inventory.SingleItem ? 1 : 6;
        var count = inventory.SingleItem ? 1 : BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if ((inventory.SingleItem ? data[0] : BinaryPrimitives.ReadInt32LittleEndian(data)) < 106)
        {
            return found;   // older item format, left alone
        }

        for (var i = 0; i < count && o < data.Length; i++)
        {
            o += 4 + 3;                       // durability, grid x/y, world level
            var flags = data[o++];
            o += (flags & 0x04) != 0 ? 2 : 0; // quality
            o += (flags & 0x08) != 0 ? 2 : 0; // stack
            o += (flags & 0x10) != 0 ? 4 : 0; // variant
            if ((flags & 0x20) != 0)
            {
                o += 8;                       // crafter id
                SkipString(data, ref o);
            }

            o += (flags & 0x40) != 0 ? 4 : 0; // prefab
            if ((flags & 0x80) != 0)
            {
                var pairs = ReadCount(data, ref o);
                for (var p = 0; p < pairs * 2; p++)
                {
                    SkipString(data, ref o);
                }
            }

            if (o >= data.Length)
            {
                break;
            }

            if ((data[o] & 1) != 0)
            {
                found.Add(inventory.Offset + o);
            }

            o++;
        }

        return found;
    }

    /// <summary>Skips a length-prefixed UTF-8 string, prefix included.</summary>
    private static void SkipString(ReadOnlySpan<byte> d, ref int o)
    {
        var length = 0;
        var shift = 0;
        byte b;
        do
        {
            b = d[o++];
            length |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        o += length;
    }

    private static int ReadCount(ReadOnlySpan<byte> d, ref int o)
    {
        int count = d[o++];
        if ((count & 0x80) != 0)
        {
            count = ((count & 0x7F) << 8) | d[o++];
        }

        return count;
    }

    private static (SaveSet Save, ChunkIndex Index, List<(ChunkIndexEntry Entry, ChunkObjects File, int Index)> Chunks)
        Load(string worldDirectory, string? worldName)
    {
        var report = WorldInspector.InspectDirectory(worldDirectory, worldName);
        if (!report.IsHealthy || report.LatestSave is not { } save || report.Index is not { } index)
        {
            throw new InvalidOperationException("O mundo precisa estar íntegro: " +
                string.Join(" ", report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
        }

        var chunks = index.Entries
            .Select((e, i) => (e, ChunkObjects.Read(Path.Combine(report.Directory, e.FileName)), i))
            .ToList();
        return (save, index, chunks);
    }
}

/// <summary>Writes a new save set (N+1) next to the current one, without touching save N.</summary>
internal static class WorldSaveWriter
{
    public static int WriteNextSave(
        string worldDirectory,
        SaveSet current,
        ChunkIndex index,
        IReadOnlyDictionary<ChunkIndexEntry, byte[]> changedChunks,
        WorldDatabase? database,
        IReadOnlyDictionary<ChunkIndexEntry, int>? newCounts = null)
    {
        var next = WorldFolder.ScanSaveSets(worldDirectory).Max(s => s.Number) + 1;
        var written = new List<string>();
        try
        {
            var entries = new List<ChunkIndexEntry>();
            foreach (var entry in index.Entries)
            {
                if (!changedChunks.TryGetValue(entry, out var bytes))
                {
                    entries.Add(entry);
                    continue;
                }

                var count = newCounts?.GetValueOrDefault(entry, entry.ZdoCount) ?? entry.ZdoCount;
                var updated = entry with { Revision = NextRevision(worldDirectory, entry), ZdoCount = count };
                written.Add(Write(Path.Combine(worldDirectory, updated.FileName), bytes));
                entries.Add(updated);
            }

            written.Add(Write(Path.Combine(worldDirectory, $"_main.{next}.db2"),
                database?.ToBytes() ?? File.ReadAllBytes(current.Db2!)));
            written.Add(Write(Path.Combine(worldDirectory, $"_main.{next}.fwl2"), File.ReadAllBytes(current.Fwl2!)));
            written.Add(Write(Path.Combine(worldDirectory, $"_main.{next}.chunks"), new ChunkIndex(index.Version, entries).ToBytes()));
            written.Add(Write(Path.Combine(worldDirectory, $"_main.{next}.ok"), File.ReadAllBytes(current.Ok!)));
            return next;
        }
        catch
        {
            // Save N was never touched; drop the partial N+1 so the world stays as it was.
            foreach (var path in Enumerable.Reverse(written))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            throw;
        }
    }

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

    private static string Write(string path, byte[] data)
    {
        if (File.Exists(path))
        {
            throw new IOException($"O arquivo {Path.GetFileName(path)} já existe; nada foi alterado.");
        }

        var temp = path + ".novo";
        File.WriteAllBytes(temp, data);
        File.Move(temp, path);
        return path;
    }
}
