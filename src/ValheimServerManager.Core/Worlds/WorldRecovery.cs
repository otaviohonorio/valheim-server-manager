using System.Buffers.Binary;
using System.Globalization;

namespace ValheimServerManager.Core.Worlds;

/// <summary>Valheim's deterministic string hash used for prefab ids.</summary>
public static class StableHash
{
    public static int Compute(string value)
    {
        unchecked
        {
            var hash1 = 5381;
            var hash2 = hash1;
            for (var i = 0; i < value.Length && value[i] != '\0'; i += 2)
            {
                hash1 = ((hash1 << 5) + hash1) ^ value[i];
                if (i == value.Length - 1 || value[i + 1] == '\0')
                {
                    break;
                }

                hash2 = ((hash2 << 5) + hash2) ^ value[i + 1];
            }

            return hash1 + (hash2 * 1566083941);
        }
    }
}

/// <summary>
/// Heuristic scan for pieces that only exist when a player built them. Used to tell a real base
/// apart from a freshly generated world with the same seed (both contain ruins with wood walls).
/// </summary>
public static class PlayerBuildScanner
{
    public static readonly IReadOnlyList<string> PlayerOnlyPrefabs =
    [
        "piece_workbench", "piece_chest_wood", "piece_chest", "portal_wood", "guard_stone",
        "smelter", "charcoal_kiln", "windmill", "forge", "piece_artisanstation",
        "piece_stonecutter", "piece_cartographytable", "piece_bed02", "piece_cookingstation",
        "piece_spinningwheel", "piece_oven", "blastfurnace", "piece_beehive", "wood_beam",
        "piece_walltorch", "hearth", "piece_sharpstakes", "Raft", "Karve", "VikingShip",
    ];

    private static readonly Dictionary<int, string> HashToPrefab =
        PlayerOnlyPrefabs.ToDictionary(StableHash.Compute, p => p);

    public static IReadOnlyDictionary<string, int> Scan(string chunkPath)
    {
        var data = File.ReadAllBytes(chunkPath);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var offset = 0; offset <= data.Length - 4; offset++)
        {
            var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
            if (HashToPrefab.TryGetValue(value, out var prefab))
            {
                counts[prefab] = counts.GetValueOrDefault(prefab) + 1;
            }
        }

        return counts;
    }

    public static IReadOnlyDictionary<string, int> ScanWorld(WorldHealthReport report)
    {
        var total = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var chunk in report.SaveSetFiles.Where(WorldFolder.IsChunkFile))
        {
            foreach (var (prefab, count) in Scan(chunk))
            {
                total[prefab] = total.GetValueOrDefault(prefab) + count;
            }
        }

        return total;
    }
}

/// <summary>
/// Rebuilds a missing <c>_main.N.chunks</c> from surviving chunk files. Each chunk header carries the
/// exact ZDO count that the index needs, so the result is byte-identical to what Valheim writes.
/// </summary>
public static class ChunkIndexRebuilder
{
    public static ChunkIndex Build(IEnumerable<string> chunkPaths)
    {
        ushort? version = null;
        var entries = new List<ChunkIndexEntry>();
        foreach (var path in chunkPaths)
        {
            if (!ChunkFileName.TryParse(path, out var x, out var z, out var flag, out var revision))
            {
                throw new InvalidDataException($"Nome de chunk inválido: {Path.GetFileName(path)}");
            }

            var header = ChunkFileHeader.Read(path);
            version ??= header.Version;
            if (header.Version != version)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(path)} está na versão {header.Version}; os outros na {version}.");
            }

            entries.Add(new ChunkIndexEntry(x, z, flag, revision, header.ZdoCount));
        }

        if (version is null)
        {
            throw new InvalidDataException("Nenhum chunk informado.");
        }

        var duplicated = entries.GroupBy(e => (e.X, e.Z)).FirstOrDefault(g => g.Count() > 1);
        if (duplicated is not null)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"Mais de uma revisão para o mesmo chunk ({duplicated.Key.X}, {duplicated.Key.Z}). Escolha só uma."));
        }

        return new ChunkIndex(version.Value, entries);
    }
}
