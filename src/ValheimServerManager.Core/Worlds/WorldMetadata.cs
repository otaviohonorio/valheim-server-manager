using System.Text;

namespace ValheimServerManager.Core.Worlds;

public sealed record WorldPlayer(string PlatformId, string Name, string CharacterName, string PlayerId)
{
    /// <summary>Steam64 id without the <c>Steam_</c> prefix, when the player came through Steam.</summary>
    public string? SteamId =>
        PlatformId.StartsWith("Steam_", StringComparison.Ordinal) ? PlatformId["Steam_".Length..] : null;
}

/// <summary>
/// Contents of a <c>_main.N.fwl2</c> world metadata file.
/// </summary>
/// <remarks>
/// Layout observed on Valheim 1.0.12 (all strings are .NET <see cref="BinaryWriter"/> strings):
/// <code>
/// i32 payload_length | i32 version | string name | string seed_name | i32 seed | i64 uid
/// i32 worldgen_version | bool flag | i32 key_count | string[key_count] keys
/// i32 player_count | (string platform_id, string name, string character, string player_id)[player_count]
/// </code>
/// Keys hold world modifiers such as <c>nobuildcost</c>, <c>resourcerate 150</c> or
/// <c>preset combat_default:...</c>.
/// </remarks>
public sealed class WorldMetadata
{
    public const string NoBuildCostKey = "nobuildcost";

    public required int Version { get; init; }
    public required string Name { get; init; }
    public required string SeedName { get; init; }
    public required int Seed { get; init; }
    public required long Uid { get; init; }
    public required int WorldGenVersion { get; init; }
    public required IReadOnlyList<string> Keys { get; init; }
    public required IReadOnlyList<WorldPlayer> Players { get; init; }

    /// <summary>Key names without their values (<c>resourcerate 150</c> → <c>resourcerate</c>).</summary>
    public IEnumerable<string> KeyNames =>
        Keys.Select(k => k.Split(' ', 2)[0]).Where(k => !k.Equals("preset", StringComparison.OrdinalIgnoreCase));

    public bool HasKey(string name) => KeyNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    public bool IsCreative => HasKey(NoBuildCostKey);

    public static WorldMetadata Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    public static WorldMetadata Parse(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        return Read(stream);
    }

    public static WorldMetadata Read(Stream stream)
    {
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            var payloadLength = reader.ReadInt32();
            if (payloadLength != stream.Length - sizeof(int))
            {
                throw new InvalidDataException(
                    $"Tamanho declarado ({payloadLength}) não confere com o arquivo ({stream.Length - sizeof(int)}).");
            }

            var version = reader.ReadInt32();
            var name = reader.ReadString();
            var seedName = reader.ReadString();
            var seed = reader.ReadInt32();
            var uid = reader.ReadInt64();
            var worldGen = reader.ReadInt32();
            _ = reader.ReadBoolean();

            var keyCount = ReadCount(reader, "chaves");
            var keys = new List<string>(keyCount);
            for (var i = 0; i < keyCount; i++)
            {
                keys.Add(reader.ReadString());
            }

            var players = new List<WorldPlayer>();
            if (stream.Position < stream.Length)
            {
                var playerCount = ReadCount(reader, "jogadores");
                for (var i = 0; i < playerCount; i++)
                {
                    players.Add(new WorldPlayer(
                        reader.ReadString(), reader.ReadString(), reader.ReadString(), reader.ReadString()));
                }
            }

            return new WorldMetadata
            {
                Version = version,
                Name = name,
                SeedName = seedName,
                Seed = seed,
                Uid = uid,
                WorldGenVersion = worldGen,
                Keys = keys,
                Players = players,
            };
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException("Arquivo .fwl2 terminou antes do esperado.", ex);
        }
    }

    private static int ReadCount(BinaryReader reader, string what)
    {
        var count = reader.ReadInt32();
        if (count is < 0 or > 10_000)
        {
            throw new InvalidDataException($"Quantidade de {what} inválida no .fwl2: {count}.");
        }

        return count;
    }
}
