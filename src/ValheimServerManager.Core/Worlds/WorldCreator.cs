using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using ValheimServerManager.Core.Localization;
using ValheimServerManager.Core.Platform;

namespace ValheimServerManager.Core.Worlds;

/// <summary>Seed rules of the in-game "new world" dialog.</summary>
public static class WorldSeed
{
    public const int MaxLength = 10;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>A random seed shaped like the ones the game generates (10 letters/digits).</summary>
    public static string Random()
    {
        Span<char> chars = stackalloc char[MaxLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    public static string? Validate(string? seed)
    {
        if (string.IsNullOrEmpty(seed))
        {
            return Strings.Seed_Required;
        }

        if (seed.Length > MaxLength)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.Seed_TooLong, MaxLength);
        }

        return seed.All(char.IsAsciiLetterOrDigit) ? null : Strings.Seed_InvalidChars;
    }
}

/// <summary>A world the game client has locally, offered as a starting point for a server.</summary>
public sealed record AvailableWorld(string Name, string SeedName, int SaveNumber, long Bytes, DateTime LastSavedUtc, string Directory);

/// <summary>
/// Prepares world folders for a new server: a brand-new world with a chosen seed, or a copy of an
/// existing world. The original folder of a copied world is only ever read.
/// </summary>
public static class WorldCreator
{
    /// <summary>
    /// Writes <c>_main.0.fwl2</c> exactly as the game's "new world" dialog does (verified byte by byte).
    /// On first start Valheim logs <c>missing _main.0.db2</c> — expected for save 0 — and generates the
    /// world from this seed.
    /// </summary>
    public static string CreateSeeded(string saveDirectory, string worldName, string seedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldName);
        if (WorldSeed.Validate(seedName) is { } problem)
        {
            throw new ArgumentException(problem, nameof(seedName));
        }

        var dir = WorldFolder.WorldDirectory(saveDirectory, worldName);
        EnsureFreeTarget(dir, worldName);
        Directory.CreateDirectory(dir);

        var metadata = new WorldMetadata
        {
            Version = WorldMetadata.CurrentVersion,
            Name = worldName,
            SeedName = seedName,
            Seed = StableHash.Compute(seedName),
            Uid = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue),
            WorldGenVersion = WorldMetadata.CurrentWorldGenVersion,
            HasBeenSaved = false,
            Keys = [],
            Players = [],
        };

        var path = Path.Combine(dir, "_main.0.fwl2");
        File.WriteAllBytes(path, metadata.ToBytes());
        return path;
    }

    /// <summary>Copies the latest complete save of a world into the server's save directory.</summary>
    public static WorldHealthReport CopyExisting(string sourceWorldDirectory, string saveDirectory)
    {
        var source = WorldInspector.InspectDirectory(sourceWorldDirectory);
        if (!source.IsHealthy || source.Metadata is null)
        {
            var reason = source.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error)?.Message
                         ?? Strings.Creator_NoCompleteSave;
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Strings.Creator_WorldUnusable, reason));
        }

        var worldName = source.Metadata.Name;
        var target = WorldFolder.WorldDirectory(saveDirectory, worldName);
        if (ValheimPaths.SameDirectory(target, sourceWorldDirectory))
        {
            throw new InvalidOperationException(Strings.Creator_AlreadyInSaveDir);
        }

        EnsureFreeTarget(target, worldName);
        var staging = target + ".copiando";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in source.SaveSetFiles)
            {
                File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
            }

            var copy = WorldInspector.InspectDirectory(staging, worldName);
            if (!copy.IsHealthy || copy.LatestSave?.Number != source.LatestSave!.Number)
            {
                throw new IOException(Strings.Creator_CopyNotIntact);
            }

            Directory.Move(staging, target);
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }

        return WorldInspector.InspectDirectory(target, worldName);
    }

    /// <summary>Complete worlds in the game client's folder (read only).</summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<AvailableWorld> ListGameWorlds() => ListWorlds(ValheimPaths.GameDataDirectory);

    public static IReadOnlyList<AvailableWorld> ListWorlds(string saveDirectory)
    {
        var result = new List<AvailableWorld>();
        foreach (var name in WorldFolder.ListWorlds(saveDirectory))
        {
            try
            {
                var report = WorldInspector.Inspect(saveDirectory, name);
                if (report is { IsHealthy: true, Metadata: { } meta, LatestSave: { } save })
                {
                    result.Add(new AvailableWorld(meta.Name, meta.SeedName, save.Number, report.SaveSetBytes,
                        report.LastSavedUtc ?? DateTime.MinValue, report.Directory));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Skip folders we cannot read.
            }
        }

        return result.OrderByDescending(w => w.LastSavedUtc).ToArray();
    }

    private static void EnsureFreeTarget(string dir, string worldName)
    {
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            throw new InvalidOperationException(
                string.Format(CultureInfo.CurrentCulture, Strings.Creator_WorldExists, worldName));
        }
    }
}
