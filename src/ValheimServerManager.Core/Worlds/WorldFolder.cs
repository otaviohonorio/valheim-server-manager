using System.Globalization;
using System.Text.RegularExpressions;

namespace ValheimServerManager.Core.Worlds;

/// <summary>The four files that make up one numbered save of a Valheim 1.0 world.</summary>
public sealed record SaveSet(int Number, string? Db2, string? Fwl2, string? Chunks, string? Ok)
{
    public bool IsComplete => Db2 is not null && Fwl2 is not null && Chunks is not null && Ok is not null;

    public IEnumerable<string> MissingParts
    {
        get
        {
            if (Db2 is null) yield return $"_main.{Number}.db2";
            if (Fwl2 is null) yield return $"_main.{Number}.fwl2";
            if (Chunks is null) yield return $"_main.{Number}.chunks";
            if (Ok is null) yield return $"_main.{Number}.ok";
        }
    }

    public IEnumerable<string> ExistingFiles =>
        new[] { Db2, Fwl2, Chunks, Ok }.Where(p => p is not null)!;
}

/// <summary>Layout helpers for a server save directory (<c>-savedir</c>).</summary>
public static partial class WorldFolder
{
    public const string WorldsDirectoryName = "worlds_local";
    public const string GameAutoBackupMarker = "_backup_auto-";

    [GeneratedRegex(@"^_main\.(?<n>\d+)\.(?<ext>db2|fwl2|chunks|ok)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaveFileRegex();

    public static string WorldsDirectory(string saveDirectory) =>
        Path.Combine(saveDirectory, WorldsDirectoryName);

    public static string WorldDirectory(string saveDirectory, string worldName) =>
        Path.Combine(WorldsDirectory(saveDirectory), worldName);

    /// <summary>World folders inside <c>worlds_local</c>, excluding Valheim's own auto backups.</summary>
    public static IReadOnlyList<string> ListWorlds(string saveDirectory)
    {
        var root = WorldsDirectory(saveDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(n => !n.Contains(GameAutoBackupMarker, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Valheim's own <c>World_backup_auto-yyyyMMdd-HHmmss</c> folders for a world.</summary>
    public static IReadOnlyList<string> ListGameAutoBackups(string saveDirectory, string worldName)
    {
        var root = WorldsDirectory(saveDirectory);
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateDirectories(root, worldName + GameAutoBackupMarker + "*")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<SaveSet> ScanSaveSets(string worldDirectory)
    {
        if (!Directory.Exists(worldDirectory))
        {
            return [];
        }

        var byNumber = new SortedDictionary<int, Dictionary<string, string>>();
        foreach (var path in Directory.EnumerateFiles(worldDirectory))
        {
            var match = SaveFileRegex().Match(Path.GetFileName(path));
            if (!match.Success)
            {
                continue;
            }

            var n = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (!byNumber.TryGetValue(n, out var parts))
            {
                parts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                byNumber[n] = parts;
            }

            parts[match.Groups["ext"].Value] = path;
        }

        return byNumber
            .Select(kv => new SaveSet(
                kv.Key,
                kv.Value.GetValueOrDefault("db2"),
                kv.Value.GetValueOrDefault("fwl2"),
                kv.Value.GetValueOrDefault("chunks"),
                kv.Value.GetValueOrDefault("ok")))
            .ToArray();
    }

    public static bool IsSaveFile(string fileName) => SaveFileRegex().IsMatch(Path.GetFileName(fileName));

    public static bool IsChunkFile(string fileName) =>
        fileName.EndsWith(ChunkFileName.Extension, StringComparison.OrdinalIgnoreCase);
}
