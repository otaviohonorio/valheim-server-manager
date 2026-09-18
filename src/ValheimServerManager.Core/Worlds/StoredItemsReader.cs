using System.Buffers.Binary;

namespace ValheimServerManager.Core.Worlds;

/// <summary>
/// Reads the item packages Valheim stores in containers, dropped items and character files
/// (<c>Inventory.Save</c> / <c>ItemDrop.ItemData.Save</c>, item version 109).
/// </summary>
/// <remarks>
/// A container or a character writes <c>i32 version | u16 count | items…</c>; a dropped item writes
/// <c>u8 version | item</c>. Each item is
/// <c>i32 durability*100 | u8 grid x | u8 grid y | u8 world level | u8 flags</c> followed by the
/// optional fields the flags announce, and ends with one byte whose bit 0 is the cheated mark.
/// </remarks>
public static class StoredItemReader
{
    /// <summary>Offsets of the item flag bytes that have the cheated bit set.</summary>
    public static IReadOnlyList<int> MarkedFlagOffsets(ReadOnlySpan<byte> data, bool singleItem)
    {
        var found = new List<int>();
        if (data.Length < 5)
        {
            return found;
        }

        var o = singleItem ? 1 : 6;
        var count = singleItem ? 1 : BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        if ((singleItem ? data[0] : BinaryPrimitives.ReadInt32LittleEndian(data)) < 106)
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
                SkipString(data, ref o);      // crafter name
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
                found.Add(o);
            }

            o++;
        }

        return found;
    }

    /// <summary>Skips a length-prefixed UTF-8 string, prefix included.</summary>
    public static void SkipString(ReadOnlySpan<byte> d, ref int o)
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
}
