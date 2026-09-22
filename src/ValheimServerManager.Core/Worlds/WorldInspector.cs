using System.Globalization;
using ValheimServerManager.Core.Localization;

namespace ValheimServerManager.Core.Worlds;

public enum IssueSeverity
{
    Info,
    Warning,
    Error,
}

public sealed record WorldIssue(IssueSeverity Severity, string Code, string Message);

/// <summary>Result of inspecting a world folder before it is handed to the server.</summary>
public sealed class WorldHealthReport
{
    public required string WorldName { get; init; }
    public required string Directory { get; init; }
    public required bool Exists { get; init; }
    public required IReadOnlyList<WorldIssue> Issues { get; init; }

    public SaveSet? LatestSave { get; init; }
    public ChunkIndex? Index { get; init; }
    public WorldMetadata? Metadata { get; init; }

    /// <summary>Every file that belongs to the latest complete save (the minimum needed to restore it).</summary>
    public IReadOnlyList<string> SaveSetFiles { get; init; } = [];

    /// <summary>Files in the folder that are neither save files nor chunks (minimap caches etc.).</summary>
    public IReadOnlyList<string> AuxiliaryFiles { get; init; } = [];

    public long SaveSetBytes { get; init; }
    public DateTime? LastSavedUtc { get; init; }

    public bool IsNewWorld => !Exists || LatestSave is null;
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == IssueSeverity.Warning);
    public bool IsHealthy => Exists && LatestSave is not null && !HasErrors;
    public int ChunkCount => Index?.Entries.Count ?? 0;
    public long TotalZdos => Index?.TotalZdos ?? 0;
}

/// <summary>
/// Checks that a world folder is safe to load. The critical rule: the highest save number present
/// must be complete. When Valheim finds <c>_main.N.fwl2</c> without <c>_main.N.db2</c>, it silently
/// generates a brand new world and later deletes the old chunks as orphans — exactly what destroyed
/// the MeuMundo world on 2026-09-16.
/// </summary>
public static class WorldInspector
{
    public static WorldHealthReport Inspect(string saveDirectory, string worldName) =>
        InspectDirectory(WorldFolder.WorldDirectory(saveDirectory, worldName), worldName);

    public static WorldHealthReport InspectDirectory(string worldDirectory, string? expectedWorldName = null)
    {
        var worldName = expectedWorldName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(worldDirectory));
        var issues = new List<WorldIssue>();

        if (!System.IO.Directory.Exists(worldDirectory))
        {
            issues.Add(new(IssueSeverity.Info, "NEW_WORLD",
                string.Format(CultureInfo.CurrentCulture, Strings.Inspector_NewWorld, worldName)));
            return new WorldHealthReport { WorldName = worldName, Directory = worldDirectory, Exists = false, Issues = issues };
        }

        var allFiles = System.IO.Directory.EnumerateFiles(worldDirectory).ToArray();
        if (allFiles.Any(f => f.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new(IssueSeverity.Info, "LEGACY_FILES",
                Strings.Inspector_LegacyFiles));
        }

        var saveSets = WorldFolder.ScanSaveSets(worldDirectory);
        if (saveSets.Count == 0)
        {
            issues.Add(new(IssueSeverity.Warning, "EMPTY_WORLD_FOLDER",
                Strings.Inspector_EmptyWorldFolder));
            return new WorldHealthReport { WorldName = worldName, Directory = worldDirectory, Exists = true, Issues = issues };
        }

        var latest = saveSets[^1];
        if (saveSets.Count == 1 && latest is { Number: 0, Fwl2: not null, Db2: null })
        {
            WorldMetadata? pending = null;
            try
            {
                pending = WorldMetadata.Read(latest.Fwl2);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                issues.Add(new(IssueSeverity.Error, "BAD_METADATA",
                    string.Format(CultureInfo.CurrentCulture, Strings.Inspector_BadMetadata, Path.GetFileName(latest.Fwl2), ex.Message)));
            }

            if (pending is not null)
            {
                issues.Add(new(IssueSeverity.Info, "NEW_WORLD_SEEDED",
                    string.Format(CultureInfo.CurrentCulture, Strings.Inspector_NewWorldSeeded, pending.SeedName)));
            }

            return new WorldHealthReport
            {
                WorldName = worldName,
                Directory = worldDirectory,
                Exists = true,
                Issues = issues,
                Metadata = pending,
            };
        }

        if (!latest.IsComplete)
        {
            var olderComplete = saveSets.LastOrDefault(s => s.IsComplete && s.Number < latest.Number);
            var hint = olderComplete is null
                ? Strings.Inspector_NoOlderCompleteSave
                : string.Format(CultureInfo.CurrentCulture, Strings.Inspector_OlderCompleteSave, olderComplete.Number, latest.Number);
            issues.Add(new(IssueSeverity.Error, "INCOMPLETE_LATEST_SAVE",
                string.Format(CultureInfo.CurrentCulture, Strings.Inspector_IncompleteLatestSave, latest.Number, string.Join(", ", latest.MissingParts), hint)));
            return new WorldHealthReport
            {
                WorldName = worldName,
                Directory = worldDirectory,
                Exists = true,
                Issues = issues,
                LatestSave = latest,
            };
        }

        if (saveSets.Count > 1)
        {
            issues.Add(new(IssueSeverity.Info, "OLD_SAVES_PRESENT",
                string.Format(CultureInfo.CurrentCulture, Strings.Inspector_OldSavesPresent, saveSets.Count - 1)));
        }

        ChunkIndex? index = null;
        try
        {
            index = ChunkIndex.Read(latest.Chunks!);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            issues.Add(new(IssueSeverity.Error, "BAD_CHUNK_INDEX",
                string.Format(CultureInfo.CurrentCulture, Strings.Inspector_BadChunkIndex, Path.GetFileName(latest.Chunks), ex.Message)));
        }

        WorldMetadata? metadata = null;
        try
        {
            metadata = WorldMetadata.Read(latest.Fwl2!);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            issues.Add(new(IssueSeverity.Error, "BAD_METADATA",
                string.Format(CultureInfo.CurrentCulture, Strings.Inspector_BadMetadata, Path.GetFileName(latest.Fwl2), ex.Message)));
        }

        if (metadata is not null && !string.Equals(metadata.Name, worldName, StringComparison.Ordinal))
        {
            issues.Add(new(IssueSeverity.Warning, "NAME_MISMATCH",
                string.Format(CultureInfo.CurrentCulture, Strings.Inspector_NameMismatch, metadata.Name, worldName)));
        }

        var saveSetFiles = latest.ExistingFiles.ToList();
        if (index is not null)
        {
            foreach (var entry in index.Entries)
            {
                var chunkPath = Path.Combine(worldDirectory, entry.FileName);
                if (!File.Exists(chunkPath))
                {
                    issues.Add(new(IssueSeverity.Error, "MISSING_CHUNK",
                        string.Format(CultureInfo.CurrentCulture, Strings.Inspector_MissingChunk, latest.Number, entry.FileName, entry.ZdoCount)));
                    continue;
                }

                saveSetFiles.Add(chunkPath);
                try
                {
                    var header = ChunkFileHeader.Read(chunkPath);
                    if (header.ZdoCount != entry.ZdoCount)
                    {
                        issues.Add(new(IssueSeverity.Error, "CHUNK_COUNT_MISMATCH",
                            string.Format(CultureInfo.CurrentCulture, Strings.Inspector_ChunkCountMismatch, entry.FileName, header.ZdoCount, entry.ZdoCount)));
                    }

                    if (header.Version != index.Version)
                    {
                        issues.Add(new(IssueSeverity.Warning, "CHUNK_VERSION_MISMATCH",
                            string.Format(CultureInfo.CurrentCulture, Strings.Inspector_ChunkVersionMismatch, entry.FileName, header.Version, index.Version)));
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
                {
                    issues.Add(new(IssueSeverity.Error, "BAD_CHUNK",
                        string.Format(CultureInfo.CurrentCulture, Strings.Inspector_BadChunk, entry.FileName, ex.Message)));
                }
            }
        }

        var auxiliary = allFiles
            .Where(f => !WorldFolder.IsSaveFile(f) && !WorldFolder.IsChunkFile(f))
            .ToArray();

        var saveBytes = saveSetFiles.Sum(f => new FileInfo(f).Length);
        var lastSaved = File.GetLastWriteTimeUtc(latest.Ok!);

        return new WorldHealthReport
        {
            WorldName = worldName,
            Directory = worldDirectory,
            Exists = true,
            Issues = issues,
            LatestSave = latest,
            Index = index,
            Metadata = metadata,
            SaveSetFiles = saveSetFiles,
            AuxiliaryFiles = auxiliary,
            SaveSetBytes = saveBytes,
            LastSavedUtc = lastSaved,
        };
    }
}
