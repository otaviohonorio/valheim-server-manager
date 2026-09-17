using System.Buffers.Binary;
using System.IO.Compression;

namespace ValheimServerManager.Core.Worlds;

/// <summary>
/// One ZDO (world object) inside a chunk file. Only what is needed to identify it is decoded; the
/// original bytes are kept so the object can be written back unchanged.
/// </summary>
/// <remarks>
/// <see cref="Prefab"/> is the StableHash of the prefab name, <see cref="Rotation"/> the encoded rotation
/// (0 without rotation), and <see cref="Offset"/>/<see cref="Length"/> locate the bytes in the file.
/// </remarks>
public readonly record struct WorldObject(int Prefab, float X, float Y, float Z, uint Rotation, int Offset, int Length, int HeaderLength)
{
    /// <summary>Built by a player: the game stores the builder in the <c>creator</c> long.</summary>
    public bool HasCreator { get; init; }

    /// <summary>Rotation around the vertical axis in degrees, or null when the object is also tilted.</summary>
    public float? Yaw
    {
        get
        {
            if (Rotation == 0)
            {
                return 0f;
            }

            if ((Rotation & FullRotationMarker) == 0)
            {
                return (Rotation & 0x7FFF) * 0.5f;
            }

            return (Rotation & 0x3FF) == 0 && ((Rotation >> 20) & 0x3FF) == 0 ? ((Rotation >> 10) & 0x3FF) * 0.5f : null;
        }
    }

    /// <summary>Set on <see cref="Rotation"/> when it was stored as three angles (4 bytes).</summary>
    internal const uint FullRotationMarker = 0x80000000;

    /// <summary>Zone (64 m square) the object belongs to, as <c>ZoneSystem.GetZone</c> computes it.</summary>
    public (short X, short Z) Zone =>
        ((short)Math.Floor((X + 32.0) / 64.0), (short)Math.Floor((Z + 32.0) / 64.0));
}

/// <summary>
/// A <c>.chunk</c> file: <c>i16 version | i32 count | ZDO[count]</c>, as written by
/// <c>ZDOMan.SaveChunk</c> / <c>ZDO.Save</c> in Valheim 1.0.
/// </summary>
/// <remarks>
/// ZDO layout: <c>u16 flags</c>; position (<c>i16 x, i16 z</c> when flag 0x2000, else 3 floats);
/// <c>i32 prefab</c>; small rotation when flag 0x1000 (2 bytes, or 4 when the high bit of the
/// first word is clear); then, when any of the low 8 bits is set: connection (<c>u8, i32</c>) and
/// float, vec3, quaternion, int, long, string and byte-array maps, each a 1–2 byte item count
/// followed by <c>(i32 key, value)</c> pairs.
/// </remarks>
public sealed class ChunkObjects
{
    private const ushort Connections = 0x01;
    private const ushort Floats = 0x02;
    private const ushort Vec3s = 0x04;
    private const ushort Quaternions = 0x08;
    private const ushort Ints = 0x10;
    private const ushort Longs = 0x20;
    private const ushort Strings = 0x40;
    private const ushort ByteArrays = 0x80;
    private const ushort RotationFlag = 0x1000;
    private const ushort SmallPosition = 0x2000;

    private ChunkObjects(short version, byte[] data, IReadOnlyList<WorldObject> objects)
    {
        Version = version;
        Data = data;
        Objects = objects;
    }

    public short Version { get; }

    public IReadOnlyList<WorldObject> Objects { get; }

    private byte[] Data { get; }

    public static ChunkObjects Read(string path) => Parse(File.ReadAllBytes(path));

    public static ChunkObjects Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            var span = data.AsSpan();
            var version = BinaryPrimitives.ReadInt16LittleEndian(span);
            var count = BinaryPrimitives.ReadInt32LittleEndian(span[2..]);
            if (count < 0)
            {
                throw new InvalidDataException($"Quantidade de objetos inválida no chunk: {count}.");
            }

            var objects = new List<WorldObject>(count);
            var o = 6;
            for (var i = 0; i < count; i++)
            {
                objects.Add(ReadObject(span, ref o));
            }

            if (o != data.Length)
            {
                throw new InvalidDataException($"O chunk tem {data.Length - o} bytes sobrando depois de {count} objetos.");
            }

            return new ChunkObjects(version, data, objects);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw new InvalidDataException("O chunk terminou antes do esperado.", ex);
        }
    }

    /// <summary>The object's key/value data (everything after position, prefab and rotation).</summary>
    public ReadOnlySpan<byte> ExtraData(WorldObject obj) =>
        Data.AsSpan(obj.Offset + obj.HeaderLength, obj.Length - obj.HeaderLength);

    /// <summary>Serializes the header plus the given objects (which must come from this file).</summary>
    public byte[] ToBytes(IReadOnlyCollection<WorldObject> keep)
    {
        ArgumentNullException.ThrowIfNull(keep);
        var buffer = new byte[6 + keep.Sum(o => o.Length)];
        BinaryPrimitives.WriteInt16LittleEndian(buffer, Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(2), keep.Count);
        var at = 6;
        foreach (var obj in keep)
        {
            Data.AsSpan(obj.Offset, obj.Length).CopyTo(buffer.AsSpan(at));
            at += obj.Length;
        }

        return buffer;
    }

    private static WorldObject ReadObject(ReadOnlySpan<byte> d, ref int o)
    {
        var start = o;
        var flags = U16(d, ref o);
        float x, y, z;
        if ((flags & SmallPosition) != 0)
        {
            x = BinaryPrimitives.ReadInt16LittleEndian(d[o..]);
            z = BinaryPrimitives.ReadInt16LittleEndian(d[(o + 2)..]);
            y = 0;
            o += 4;
        }
        else
        {
            x = BinaryPrimitives.ReadSingleLittleEndian(d[o..]);
            y = BinaryPrimitives.ReadSingleLittleEndian(d[(o + 4)..]);
            z = BinaryPrimitives.ReadSingleLittleEndian(d[(o + 8)..]);
            o += 12;
        }

        var prefab = BinaryPrimitives.ReadInt32LittleEndian(d[o..]);
        o += 4;

        uint rotation = 0;
        if ((flags & RotationFlag) != 0)
        {
            rotation = U16(d, ref o);
            if ((rotation & 0x8000) == 0)
            {
                rotation = (rotation << 16) | U16(d, ref o) | WorldObject.FullRotationMarker;
            }
        }

        var headerLength = o - start;
        var hasCreator = false;
        if ((flags & 0xFF) != 0)
        {
            if ((flags & Connections) != 0)
            {
                o += 5;
            }

            SkipMap(d, ref o, flags, Floats, 4);
            SkipMap(d, ref o, flags, Vec3s, 12);
            SkipMap(d, ref o, flags, Quaternions, 16);
            SkipMap(d, ref o, flags, Ints, 4);
            hasCreator = (flags & Longs) != 0 && MapHasKey(d, o, flags, CreatorKey);
            SkipMap(d, ref o, flags, Longs, 8);
            SkipMap(d, ref o, flags, Strings, StringValue);
            SkipMap(d, ref o, flags, ByteArrays, ByteArrayValue);
        }

        if (o > d.Length)
        {
            throw new InvalidDataException("O chunk terminou no meio de um objeto.");
        }

        return new WorldObject(prefab, x, y, z, rotation, start, o - start, headerLength) { HasCreator = hasCreator };
    }

    private static readonly int CreatorKey = StableHash.Compute("creator");

    /// <summary>Whether the long map starting at <paramref name="o"/> has <paramref name="key"/>.</summary>
    private static bool MapHasKey(ReadOnlySpan<byte> d, int o, ushort flags, int key)
    {
        int count = d[o++];
        if ((count & 0x80) != 0)
        {
            count = ((count & 0x7F) << 8) | d[o++];
        }

        for (var i = 0; i < count; i++, o += 12)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(d[o..]) == key)
            {
                return true;
            }
        }

        return false;
    }

    private const int StringValue = -1;
    private const int ByteArrayValue = -2;

    /// <summary>Skips a key/value map; <paramref name="valueSize"/> is a byte count or one of the variable-size markers.</summary>
    private static void SkipMap(ReadOnlySpan<byte> d, ref int o, ushort flags, ushort flag, int valueSize)
    {
        if ((flags & flag) == 0)
        {
            return;
        }

        int count = d[o++];
        if ((count & 0x80) != 0)
        {
            count = ((count & 0x7F) << 8) | d[o++];
        }

        for (var i = 0; i < count; i++)
        {
            o += 4;
            o += valueSize switch
            {
                StringValue => StringSize(d, o),
                ByteArrayValue => 4 + BinaryPrimitives.ReadInt32LittleEndian(d[o..]),
                _ => valueSize,
            };
        }
    }

    private static int StringSize(ReadOnlySpan<byte> d, int offset)
    {
        int length = 0, shift = 0, at = offset;
        byte b;
        do
        {
            b = d[at++];
            length |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        return at - offset + length;
    }

    private static ushort U16(ReadOnlySpan<byte> d, ref int o)
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);
        o += 2;
        return v;
    }
}

/// <summary>
/// The zone part of <c>_main.N.db2</c>: <c>i32 version | f64 net_time | i32 size | gzip(zone data) |
/// random-event and persistent-event data</c>. Zone data starts with the list of generated zones;
/// only that list is decoded, everything else is copied as is.
/// </summary>
public sealed class WorldDatabase
{
    private readonly byte[] _head;
    private readonly byte[] _zoneRest;
    private readonly byte[] _tail;

    private WorldDatabase(byte[] head, List<(short X, short Z)> zones, byte[] zoneRest, byte[] tail)
    {
        _head = head;
        GeneratedZones = zones;
        _zoneRest = zoneRest;
        _tail = tail;
    }

    /// <summary>Zones Valheim will not generate again (vegetation, locations and spawn control are already there).</summary>
    public List<(short X, short Z)> GeneratedZones { get; }

    public static WorldDatabase Read(string path) => Parse(File.ReadAllBytes(path));

    public static WorldDatabase Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            return ParseCore(data);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
        {
            throw new InvalidDataException("O bloco de zonas do .db2 não pôde ser lido.", ex);
        }
    }

    private static WorldDatabase ParseCore(byte[] data)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("Arquivo .db2 muito curto.");
        }

        var size = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        if (size < 0 || 16 + size > data.Length)
        {
            throw new InvalidDataException($"Tamanho do bloco de zonas inválido no .db2: {size}.");
        }

        byte[] payload;
        using (var input = new GZipStream(new MemoryStream(data, 16, size), CompressionMode.Decompress))
        using (var output = new MemoryStream())
        {
            input.CopyTo(output);
            payload = output.ToArray();
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0 || 4 + ((long)count * 4) > payload.Length)
        {
            throw new InvalidDataException($"Quantidade de zonas inválida no .db2: {count}.");
        }

        var zones = new List<(short, short)>(count);
        for (var i = 0; i < count; i++)
        {
            zones.Add((BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(4 + (i * 4))),
                       BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(6 + (i * 4)))));
        }

        return new WorldDatabase(data[..12], zones, payload[(4 + (count * 4))..], data[(16 + size)..]);
    }

    public byte[] ToBytes()
    {
        var payload = new byte[4 + (GeneratedZones.Count * 4) + _zoneRest.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, GeneratedZones.Count);
        for (var i = 0; i < GeneratedZones.Count; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(4 + (i * 4)), GeneratedZones[i].X);
            BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(6 + (i * 4)), GeneratedZones[i].Z);
        }

        _zoneRest.CopyTo(payload, 4 + (GeneratedZones.Count * 4));

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        using var file = new MemoryStream();
        file.Write(_head);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, (int)compressed.Length);
        file.Write(size);
        compressed.Position = 0;
        compressed.CopyTo(file);
        file.Write(_tail);
        return file.ToArray();
    }
}
