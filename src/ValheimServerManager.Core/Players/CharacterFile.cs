using System.Buffers.Binary;
using System.Security.Cryptography;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Players;

public sealed record CharacterCheatMarks(string Name, int Items, int MarkedItems)
{
    public bool NeedsCleaning => MarkedItems > 0;
}

/// <summary>
/// A character file (<c>.fch</c>), so the "made with cheats" mark can also be cleared from what a
/// player carries. The world copy of an item is cleaned by <see cref="WorldCheatMarks"/>, but a
/// backpack lives in this file, on the player's own machine (Steam cloud folder or
/// <c>AppData\LocalLow\IronGate\Valheim\characters</c>).
/// </summary>
/// <remarks>
/// Layout: <c>i32 length | package | i32 hash length | SHA-512 of the package</c>. The player data
/// is the package's last field (<c>bool true | i32 length | bytes</c>) and starts with
/// <c>i32 version (33) | f32 max health | f32 health | f32 max stamina | f32 time since death |
/// string guardian power | f32 cooldown</c>, followed by the inventory exactly as a container
/// stores it. Only the flag bits change, so the file keeps its size and every other byte.
/// </remarks>
public static class CharacterFile
{
    public static CharacterCheatMarks Scan(string path)
    {
        var (package, inventory) = Read(path);
        var flags = StoredItemReader.MarkedFlagOffsets(package.AsSpan(inventory.Offset, inventory.Length), singleItem: false);
        return new CharacterCheatMarks(Path.GetFileNameWithoutExtension(path), inventory.Count, flags.Count);
    }

    /// <summary>Clears every mark and rewrites the file (the caller should back it up first).</summary>
    public static CharacterCheatMarks Clean(string path)
    {
        var (package, inventory) = Read(path);
        var flags = StoredItemReader.MarkedFlagOffsets(package.AsSpan(inventory.Offset, inventory.Length), singleItem: false);
        if (flags.Count > 0)
        {
            foreach (var at in flags)
            {
                package[inventory.Offset + at] &= 0xFE;
            }

            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(package.Length);
                w.Write(package);
                var hash = SHA512.HashData(package);
                w.Write(hash.Length);
                w.Write(hash);
            }

            var temp = path + ".novo";
            File.WriteAllBytes(temp, file.ToArray());
            File.Move(temp, path, overwrite: true);
        }

        return new CharacterCheatMarks(Path.GetFileNameWithoutExtension(path), inventory.Count, flags.Count);
    }

    private static (byte[] Package, (int Offset, int Length, int Count) Inventory) Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8)
        {
            throw new InvalidDataException("Arquivo de personagem muito curto.");
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (length <= 0 || 4 + length > data.Length)
        {
            throw new InvalidDataException("Arquivo de personagem com tamanho inválido.");
        }

        var package = data[4..(4 + length)];
        var playerData = FindPlayerData(package)
            ?? throw new InvalidDataException("Não encontrei os dados do jogador no arquivo.");

        var o = playerData.Offset;
        var version = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(o));
        if (version < 33)
        {
            throw new InvalidDataException($"Versão de personagem não suportada: {version}.");
        }

        o += 4 + (4 * 4);                         // version, max health, health, max stamina, time since death
        StoredItemReader.SkipString(package, ref o);   // guardian power
        o += 4;                                   // guardian power cooldown

        var itemsVersion = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(o));
        if (itemsVersion is < 106 or > 200)
        {
            throw new InvalidDataException($"Inventário em formato inesperado (versão {itemsVersion}).");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(package.AsSpan(o + 4));
        var end = playerData.Offset + playerData.Length;
        return (package, (o, end - o, count));
    }

    /// <summary>The player data is the package's last field: <c>bool true | i32 length | bytes</c>.</summary>
    private static (int Offset, int Length)? FindPlayerData(byte[] package)
    {
        for (var q = package.Length - 4; q > 0; q--)
        {
            var n = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(q));
            if (n > 0 && q + 4 + n == package.Length && package[q - 1] == 1)
            {
                return (q + 4, n);
            }
        }

        return null;
    }
}
