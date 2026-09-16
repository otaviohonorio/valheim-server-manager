using System.Buffers.Binary;
using System.Globalization;

namespace ValheimServerManager.Core.Worlds;

/// <summary>
/// One entry of a <c>_main.N.chunks</c> index: which chunk file revision belongs to the save
/// and how many ZDOs (world objects) it holds.
/// </summary>
public sealed record ChunkIndexEntry(sbyte X, sbyte Z, byte Flag, int Revision, int ZdoCount)
{
    public string FileName => ChunkFileName.Format(X, Z, Flag, Revision);
}

/// <summary>
/// The <c>_main.N.chunks</c> file introduced by Valheim 1.0 (world version 41).
/// </summary>
/// <remarks>
/// Layout (little endian), confirmed byte-for-byte against real saves:
/// <code>
/// header (10 bytes): u16 version | u32 total_zdos | u32 entry_count
/// entry  (11 bytes): i8 x | i8 z | u8 flag | u32 revision | u32 zdo_count
/// </code>
/// Entries are ordered by (x, z). The chunk file for an entry is named
/// <c>{z:x2}_{x:x2}__{flag}_{revision}.chunk</c>.
/// </remarks>
public sealed class ChunkIndex
{
    public const int HeaderSize = 10;
    public const int EntrySize = 11;

    public ChunkIndex(ushort version, IEnumerable<ChunkIndexEntry> entries)
    {
        Version = version;
        Entries = entries.OrderBy(e => e.X).ThenBy(e => e.Z).ToArray();
    }

    public ushort Version { get; }

    public IReadOnlyList<ChunkIndexEntry> Entries { get; }

    public long TotalZdos => Entries.Sum(e => (long)e.ZdoCount);

    public static ChunkIndex Read(string path) => Parse(File.ReadAllBytes(path));

    public static ChunkIndex Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"Índice de chunks muito curto ({data.Length} bytes; mínimo {HeaderSize}).");
        }

        var version = BinaryPrimitives.ReadUInt16LittleEndian(data);
        var declaredTotal = BinaryPrimitives.ReadUInt32LittleEndian(data[2..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data[6..]);

        var expectedLength = HeaderSize + ((long)count * EntrySize);
        if (data.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"Índice de chunks declara {count} entradas ({expectedLength} bytes), mas tem {data.Length} bytes.");
        }

        var entries = new List<ChunkIndexEntry>((int)count);
        for (var i = 0; i < count; i++)
        {
            var e = data.Slice(HeaderSize + (i * EntrySize), EntrySize);
            entries.Add(new ChunkIndexEntry(
                X: unchecked((sbyte)e[0]),
                Z: unchecked((sbyte)e[1]),
                Flag: e[2],
                Revision: BinaryPrimitives.ReadInt32LittleEndian(e[3..]),
                ZdoCount: BinaryPrimitives.ReadInt32LittleEndian(e[7..])));
        }

        var index = new ChunkIndex(version, entries);
        if (index.TotalZdos != declaredTotal)
        {
            throw new InvalidDataException(
                $"Índice de chunks declara {declaredTotal} objetos no total, mas as entradas somam {index.TotalZdos}.");
        }

        return index;
    }

    public byte[] ToBytes()
    {
        var buffer = new byte[HeaderSize + (Entries.Count * EntrySize)];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[2..], checked((uint)TotalZdos));
        BinaryPrimitives.WriteUInt32LittleEndian(span[6..], (uint)Entries.Count);

        for (var i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            var e = span.Slice(HeaderSize + (i * EntrySize), EntrySize);
            e[0] = unchecked((byte)entry.X);
            e[1] = unchecked((byte)entry.Z);
            e[2] = entry.Flag;
            BinaryPrimitives.WriteInt32LittleEndian(e[3..], entry.Revision);
            BinaryPrimitives.WriteInt32LittleEndian(e[7..], entry.ZdoCount);
        }

        return buffer;
    }
}

/// <summary>Naming convention of Valheim 1.0 chunk files.</summary>
public static class ChunkFileName
{
    public const string Extension = ".chunk";

    public static string Format(sbyte x, sbyte z, byte flag, int revision) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{unchecked((byte)z):x2}_{unchecked((byte)x):x2}__{flag}_{revision}{Extension}");

    /// <summary>Parses <c>20_1e__1_11.chunk</c> into its components.</summary>
    public static bool TryParse(string fileName, out sbyte x, out sbyte z, out byte flag, out int revision)
    {
        x = z = 0;
        flag = 0;
        revision = 0;

        var name = Path.GetFileName(fileName);
        if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = name[..^Extension.Length];
        var halves = stem.Split("__");
        if (halves.Length != 2)
        {
            return false;
        }

        var coords = halves[0].Split('_');
        var rev = halves[1].Split('_');
        if (coords.Length != 2 || rev.Length != 2)
        {
            return false;
        }

        if (!byte.TryParse(coords[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var zb) ||
            !byte.TryParse(coords[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var xb) ||
            !byte.TryParse(rev[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out flag) ||
            !int.TryParse(rev[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out revision))
        {
            return false;
        }

        x = unchecked((sbyte)xb);
        z = unchecked((sbyte)zb);
        return true;
    }
}

/// <summary>First bytes of a <c>.chunk</c> file: world version and number of ZDOs inside.</summary>
public readonly record struct ChunkFileHeader(ushort Version, int ZdoCount)
{
    public const int Size = 6;

    public static ChunkFileHeader Read(string path)
    {
        Span<byte> head = stackalloc byte[Size];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.ReadExactly(head);
        return Parse(head);
    }

    public static ChunkFileHeader Parse(ReadOnlySpan<byte> head)
    {
        if (head.Length < Size)
        {
            throw new InvalidDataException("Arquivo de chunk sem cabeçalho.");
        }

        return new ChunkFileHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(head),
            BinaryPrimitives.ReadInt32LittleEndian(head[2..]));
    }
}
