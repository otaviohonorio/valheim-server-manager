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
                $"A pasta do mundo \"{worldName}\" não existe. O servidor vai criar um mundo NOVO, com seed aleatória."));
            return new WorldHealthReport { WorldName = worldName, Directory = worldDirectory, Exists = false, Issues = issues };
        }

        var allFiles = System.IO.Directory.EnumerateFiles(worldDirectory).ToArray();
        if (allFiles.Any(f => f.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".fwl", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new(IssueSeverity.Info, "LEGACY_FILES",
                "Há arquivos no formato antigo (.db/.fwl). O Valheim 1.0 converte na primeira gravação."));
        }

        var saveSets = WorldFolder.ScanSaveSets(worldDirectory);
        if (saveSets.Count == 0)
        {
            issues.Add(new(IssueSeverity.Warning, "EMPTY_WORLD_FOLDER",
                "A pasta do mundo existe, mas não tem nenhum save (_main.N.*). O servidor vai gerar um mundo NOVO."));
            return new WorldHealthReport { WorldName = worldName, Directory = worldDirectory, Exists = true, Issues = issues };
        }

        var latest = saveSets[^1];
        if (!latest.IsComplete)
        {
            var olderComplete = saveSets.LastOrDefault(s => s.IsComplete && s.Number < latest.Number);
            var hint = olderComplete is null
                ? "Não há nenhum save completo anterior nesta pasta."
                : $"Existe o save {olderComplete.Number} completo, mas o jogo tentaria carregar o {latest.Number}.";
            issues.Add(new(IssueSeverity.Error, "INCOMPLETE_LATEST_SAVE",
                $"O save mais recente ({latest.Number}) está incompleto: falta {string.Join(", ", latest.MissingParts)}. " +
                $"Se o servidor subir assim, ele gera um mundo NOVO por cima e apaga as construções antigas. {hint} " +
                "Restaure um backup antes de iniciar."));
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
                $"Há {saveSets.Count - 1} save(s) antigo(s) na pasta; o Valheim apaga esses arquivos ao carregar."));
        }

        ChunkIndex? index = null;
        try
        {
            index = ChunkIndex.Read(latest.Chunks!);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            issues.Add(new(IssueSeverity.Error, "BAD_CHUNK_INDEX",
                $"O índice {Path.GetFileName(latest.Chunks)} está corrompido: {ex.Message}"));
        }

        WorldMetadata? metadata = null;
        try
        {
            metadata = WorldMetadata.Read(latest.Fwl2!);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            issues.Add(new(IssueSeverity.Error, "BAD_METADATA",
                $"O arquivo {Path.GetFileName(latest.Fwl2)} está corrompido: {ex.Message}"));
        }

        if (metadata is not null && !string.Equals(metadata.Name, worldName, StringComparison.Ordinal))
        {
            issues.Add(new(IssueSeverity.Warning, "NAME_MISMATCH",
                $"O nome gravado no mundo é \"{metadata.Name}\", mas a pasta se chama \"{worldName}\" (maiúsculas contam)."));
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
                        $"O save {latest.Number} referencia {entry.FileName} ({entry.ZdoCount} objetos), mas o arquivo não existe."));
                    continue;
                }

                saveSetFiles.Add(chunkPath);
                try
                {
                    var header = ChunkFileHeader.Read(chunkPath);
                    if (header.ZdoCount != entry.ZdoCount)
                    {
                        issues.Add(new(IssueSeverity.Error, "CHUNK_COUNT_MISMATCH",
                            $"{entry.FileName} tem {header.ZdoCount} objetos, mas o índice espera {entry.ZdoCount}."));
                    }

                    if (header.Version != index.Version)
                    {
                        issues.Add(new(IssueSeverity.Warning, "CHUNK_VERSION_MISMATCH",
                            $"{entry.FileName} está na versão {header.Version}, o índice na {index.Version}."));
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
                {
                    issues.Add(new(IssueSeverity.Error, "BAD_CHUNK",
                        $"Não foi possível ler {entry.FileName}: {ex.Message}"));
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
