using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Backups;

/// <summary>
/// Verified world backups. A backup holds exactly one complete save (the four <c>_main.N.*</c>
/// files plus the chunks its index references), so restoring can never mix files from different
/// saves. Files of a finished save are never rewritten by Valheim (each save uses new names), which
/// makes it safe to copy them while the server is running.
/// </summary>
public sealed partial class BackupService(TimeProvider time, ILogger<BackupService>? logger = null)
{
    public const string ReplacedFolderName = "_substituidos";
    private const string PartialSuffix = ".parcial";
    private readonly ILogger _logger = logger ?? NullLogger<BackupService>.Instance;

    [GeneratedRegex(@"^(?<world>.+)_(?<ts>\d{8}-\d{6})_save(?<n>\d+)(?:_(?<kind>[a-z-]+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"_backup_auto-(?<ts>\d{8}-\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex GameAutoRegex();

    public static string KindSlug(BackupKind kind) => kind switch
    {
        BackupKind.Manual => "manual",
        BackupKind.PreStart => "antes-de-iniciar",
        BackupKind.PostStop => "depois-de-parar",
        BackupKind.PreRestore => "antes-de-restaurar",
        BackupKind.Quarantine => "quarentena",
        BackupKind.GameAuto => "valheim-auto",
        _ => "importado",
    };

    private static BackupKind KindFromSlug(string? slug) => slug?.ToLowerInvariant() switch
    {
        "manual" => BackupKind.Manual,
        "antes-de-iniciar" => BackupKind.PreStart,
        "depois-de-parar" => BackupKind.PostStop,
        "antes-de-restaurar" => BackupKind.PreRestore,
        "quarentena" => BackupKind.Quarantine,
        _ => BackupKind.Imported,
    };

    public async Task<BackupEntry> CreateAsync(ServerProfile profile, BackupKind kind, string? note = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var root = profile.EffectiveBackupDirectory;
        Directory.CreateDirectory(root);

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var report = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName);
            if (!report.Exists || report.LatestSave is null)
            {
                throw new InvalidOperationException($"O mundo \"{profile.WorldName}\" ainda não tem save para copiar.");
            }

            if (report.HasErrors)
            {
                throw new InvalidOperationException(
                    "O mundo tem problemas e não pode ser copiado como backup válido: " +
                    string.Join(" ", report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
            }

            try
            {
                return await CopySaveSetAsync(profile, report, kind, note, root, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException ex)
            {
                // The server finished a new save and cleaned old files mid-copy. Re-inspect and retry.
                last = ex;
                _logger.LogWarning("Arquivo sumiu durante o backup (tentativa {Attempt}): {File}", attempt, ex.FileName);
                await Task.Delay(TimeSpan.FromSeconds(2), time, ct).ConfigureAwait(false);
            }
        }

        throw new IOException("O backup não conseguiu uma cópia consistente após 3 tentativas.", last);
    }

    private async Task<BackupEntry> CopySaveSetAsync(
        ServerProfile profile, WorldHealthReport report, BackupKind kind, string? note, string root, CancellationToken ct)
    {
        var now = time.GetLocalNow();
        var name = UniqueName(root, $"{profile.WorldName}_{now:yyyyMMdd-HHmmss}_save{report.LatestSave!.Number}_{KindSlug(kind)}");
        var final = Path.Combine(root, name);
        var partial = final + PartialSuffix;
        Directory.CreateDirectory(partial);

        try
        {
            var manifest = new BackupManifest
            {
                WorldName = profile.WorldName,
                SaveNumber = report.LatestSave.Number,
                Kind = kind,
                CreatedAt = now,
                WorldSavedUtc = report.LastSavedUtc,
                ProfileId = profile.Id,
                ProfileName = profile.DisplayName,
                Note = note,
                TotalZdos = report.TotalZdos,
                ChunkCount = report.ChunkCount,
                CreativeKey = report.Metadata?.IsCreative ?? false,
                AppVersion = AppVersion,
            };

            foreach (var source in report.SaveSetFiles.Concat(report.AuxiliaryFiles))
            {
                var required = !report.AuxiliaryFiles.Contains(source);
                var fileName = Path.GetFileName(source);
                try
                {
                    var (size, hash) = await CopyWithHashAsync(source, Path.Combine(partial, fileName), ct).ConfigureAwait(false);
                    manifest.Files.Add(new BackupFile(fileName, size, hash));
                }
                catch (FileNotFoundException) when (!required)
                {
                    // Caches may vanish; they are optional.
                }
            }

            await WriteManifestAsync(partial, manifest, ct).ConfigureAwait(false);

            var verification = await VerifyDirectoryAsync(partial, manifest, ct).ConfigureAwait(false);
            if (!verification.Ok)
            {
                throw new IOException("O backup não passou na verificação: " + string.Join(" ", verification.Problems));
            }

            Directory.Move(partial, final);
            _logger.LogInformation("Backup {Name} criado ({Files} arquivos, save {Save})", name, manifest.Files.Count, manifest.SaveNumber);

            var entry = ToEntry(final, manifest);
            if (entry.IsAutomatic)
            {
                ApplyRetention(profile);
            }

            return entry;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Copies a whole (possibly broken) world folder as-is, for later analysis.</summary>
    public async Task<BackupEntry?> QuarantineAsync(ServerProfile profile, string reason, CancellationToken ct = default)
    {
        var worldDir = WorldFolder.WorldDirectory(profile.SaveDirectory, profile.WorldName);
        if (!Directory.Exists(worldDir) || !Directory.EnumerateFileSystemEntries(worldDir).Any())
        {
            return null;
        }

        var root = profile.EffectiveBackupDirectory;
        var now = time.GetLocalNow();
        var final = Path.Combine(root, UniqueName(root, $"{profile.WorldName}_{now:yyyyMMdd-HHmmss}_save0_{KindSlug(BackupKind.Quarantine)}"));
        Directory.CreateDirectory(final);
        var manifest = new BackupManifest
        {
            WorldName = profile.WorldName,
            Kind = BackupKind.Quarantine,
            CreatedAt = now,
            ProfileId = profile.Id,
            ProfileName = profile.DisplayName,
            Note = reason,
            AppVersion = AppVersion,
        };

        foreach (var file in Directory.EnumerateFiles(worldDir))
        {
            var (size, hash) = await CopyWithHashAsync(file, Path.Combine(final, Path.GetFileName(file)), ct).ConfigureAwait(false);
            manifest.Files.Add(new BackupFile(Path.GetFileName(file), size, hash));
        }

        await WriteManifestAsync(final, manifest, ct).ConfigureAwait(false);
        return ToEntry(final, manifest);
    }

    public IReadOnlyList<BackupEntry> List(ServerProfile profile, bool includeGameAutoBackups = true)
    {
        var entries = new List<BackupEntry>();
        var root = profile.EffectiveBackupDirectory;
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(ReplacedFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var entry = ReadEntry(dir);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        if (includeGameAutoBackups && !string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            foreach (var dir in WorldFolder.ListGameAutoBackups(profile.SaveDirectory, profile.WorldName))
            {
                var report = WorldInspector.InspectDirectory(dir, profile.WorldName);
                var m = GameAutoRegex().Match(Path.GetFileName(dir));
                var created = m.Success && DateTime.TryParseExact(m.Groups["ts"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts)
                    ? new DateTimeOffset(ts)
                    : new DateTimeOffset(Directory.GetLastWriteTime(dir));
                entries.Add(new BackupEntry(dir, profile.WorldName, report.LatestSave?.Number, BackupKind.GameAuto, created, DirectorySize(dir), null));
            }
        }

        return entries.OrderByDescending(e => e.CreatedAt).ToArray();
    }

    public BackupEntry? ReadEntry(string directory)
    {
        var manifestPath = Path.Combine(directory, BackupManifest.FileName);
        if (File.Exists(manifestPath))
        {
            try
            {
                using var stream = File.OpenRead(manifestPath);
                var manifest = JsonSerializer.Deserialize(stream, BackupJsonContext.Default.BackupManifest);
                if (manifest is not null)
                {
                    return ToEntry(directory, manifest);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Manifesto ilegível em {Dir}", directory);
            }
        }

        var saves = WorldFolder.ScanSaveSets(directory);
        if (saves.Count == 0)
        {
            return null;
        }

        var match = NameRegex().Match(Path.GetFileName(directory));
        var world = match.Success ? match.Groups["world"].Value : Path.GetFileName(directory);
        var created = match.Success && DateTime.TryParseExact(match.Groups["ts"].Value, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var ts)
            ? new DateTimeOffset(ts)
            : new DateTimeOffset(Directory.GetLastWriteTime(directory));
        var kind = match.Success ? KindFromSlug(match.Groups["kind"].Value) : BackupKind.Imported;
        return new BackupEntry(directory, world, saves[^1].Number, kind, created, DirectorySize(directory), null);
    }

    public async Task<BackupVerification> VerifyAsync(BackupEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Manifest is not null)
        {
            return await VerifyDirectoryAsync(entry.Directory, entry.Manifest, ct).ConfigureAwait(false);
        }

        var report = WorldInspector.InspectDirectory(entry.Directory, entry.WorldName);
        var problems = report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message).ToList();
        if (report.LatestSave is null)
        {
            problems.Add("Nenhum save encontrado.");
        }

        return new BackupVerification(problems.Count == 0, problems);
    }

    private static async Task<BackupVerification> VerifyDirectoryAsync(string directory, BackupManifest manifest, CancellationToken ct)
    {
        var problems = new List<string>();
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Name);
            if (!File.Exists(path))
            {
                problems.Add($"Falta {file.Name}.");
                continue;
            }

            var hash = await HashFileAsync(path, ct).ConfigureAwait(false);
            if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{file.Name} está diferente do original.");
            }
        }

        if (manifest.Kind != BackupKind.Quarantine)
        {
            var report = WorldInspector.InspectDirectory(directory, manifest.WorldName);
            problems.AddRange(report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message));
            if (report.LatestSave?.Number != manifest.SaveNumber)
            {
                problems.Add($"O backup deveria conter o save {manifest.SaveNumber}.");
            }
        }

        return new BackupVerification(problems.Count == 0, problems);
    }

    /// <summary>
    /// Replaces the world with a backup. The caller must guarantee the server is stopped.
    /// The current world is first saved as a <see cref="BackupKind.PreRestore"/> backup (or quarantined
    /// when broken) and then moved aside — nothing is deleted.
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(ServerProfile profile, BackupEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Kind == BackupKind.Quarantine)
        {
            throw new InvalidOperationException("Cópias de quarentena guardam um mundo com defeito e não podem ser restauradas.");
        }

        if (!entry.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Este backup é do mundo \"{entry.WorldName}\", e o perfil usa \"{profile.WorldName}\". " +
                "Troque o mundo do perfil antes de restaurar.");
        }

        var verification = await VerifyAsync(entry, ct).ConfigureAwait(false);
        if (!verification.Ok)
        {
            throw new InvalidOperationException("O backup escolhido não passou na verificação: " + string.Join(" ", verification.Problems));
        }

        var source = WorldInspector.InspectDirectory(entry.Directory, entry.WorldName);
        var worldDir = WorldFolder.WorldDirectory(profile.SaveDirectory, profile.WorldName);

        BackupEntry? safety = null;
        string? replaced = null;
        if (Directory.Exists(worldDir) && Directory.EnumerateFileSystemEntries(worldDir).Any())
        {
            var current = WorldInspector.InspectDirectory(worldDir, profile.WorldName);
            safety = current.IsHealthy
                ? await CreateAsync(profile, BackupKind.PreRestore, $"Antes de restaurar {entry.Name}", ct).ConfigureAwait(false)
                : await QuarantineAsync(profile, $"Mundo com defeito substituído por {entry.Name}", ct).ConfigureAwait(false);

            var replacedRoot = Path.Combine(profile.EffectiveBackupDirectory, ReplacedFolderName);
            Directory.CreateDirectory(replacedRoot);
            replaced = Path.Combine(replacedRoot, UniqueName(replacedRoot, $"{profile.WorldName}_{time.GetLocalNow():yyyyMMdd-HHmmss}"));
            Directory.Move(worldDir, replaced);
        }

        var staging = worldDir + PartialSuffix;
        TryDelete(staging);
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var file in source.SaveSetFiles.Concat(source.AuxiliaryFiles))
            {
                if (Path.GetFileName(file).Equals(BackupManifest.FileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await CopyWithHashAsync(file, Path.Combine(staging, Path.GetFileName(file)), ct).ConfigureAwait(false);
            }

            var restored = WorldInspector.InspectDirectory(staging, profile.WorldName);
            if (!restored.IsHealthy)
            {
                throw new IOException("A cópia restaurada não passou na inspeção: " +
                    string.Join(" ", restored.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
            }

            Directory.Move(staging, worldDir);
        }
        catch
        {
            TryDelete(staging);
            if (replaced is not null && !Directory.Exists(worldDir))
            {
                Directory.Move(replaced, worldDir);
            }

            throw;
        }

        _logger.LogInformation("Mundo {World} restaurado de {Backup}", profile.WorldName, entry.Name);
        return new RestoreResult(entry, safety, replaced);
    }

    public void Delete(ServerProfile profile, BackupEntry entry)
    {
        var root = Path.GetFullPath(profile.EffectiveBackupDirectory);
        var target = Path.GetFullPath(entry.Directory);
        if (entry.Kind == BackupKind.GameAuto || !target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Só é possível excluir backups da pasta de backups do gerenciador.");
        }

        Directory.Delete(target, recursive: true);
    }

    public int ApplyRetention(ServerProfile profile)
    {
        var removed = 0;
        foreach (var old in List(profile, includeGameAutoBackups: false)
                     .Where(e => e.IsAutomatic && e.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(e => e.CreatedAt)
                     .Skip(profile.AutoBackupRetention))
        {
            TryDelete(old.Directory);
            removed++;
        }

        return removed;
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<(long Size, string Hash)> CopyWithHashAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(source));
        return (total, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    private static async Task WriteManifestAsync(string directory, BackupManifest manifest, CancellationToken ct)
    {
        await using var stream = File.Create(Path.Combine(directory, BackupManifest.FileName));
        await JsonSerializer.SerializeAsync(stream, manifest, BackupJsonContext.Default.BackupManifest, ct).ConfigureAwait(false);
    }

    private static BackupEntry ToEntry(string directory, BackupManifest manifest) =>
        new(directory, manifest.WorldName, manifest.Kind == BackupKind.Quarantine ? null : manifest.SaveNumber,
            manifest.Kind, manifest.CreatedAt, manifest.TotalBytes, manifest);

    private static string UniqueName(string root, string baseName)
    {
        var name = baseName;
        var i = 2;
        while (Directory.Exists(Path.Combine(root, name)) || Directory.Exists(Path.Combine(root, name + PartialSuffix)))
        {
            name = $"{baseName}-{i++}";
        }

        return name;
    }

    private static long DirectorySize(string dir) =>
        Directory.EnumerateFiles(dir).Sum(f => new FileInfo(f).Length);

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string AppVersion =>
        typeof(BackupService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}
