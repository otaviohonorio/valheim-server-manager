using System.Text.Json.Serialization;

namespace ValheimServerManager.Core.Backups;

public enum BackupKind
{
    /// <summary>Requested by the user.</summary>
    Manual,

    /// <summary>Taken automatically right before the server starts.</summary>
    PreStart,

    /// <summary>Taken automatically after the server stopped.</summary>
    PostStop,

    /// <summary>Snapshot of the current world taken before a restore replaced it.</summary>
    PreRestore,

    /// <summary>Raw copy of a world folder that failed inspection (kept for forensics).</summary>
    Quarantine,

    /// <summary>Folder found in the backup directory without a manifest.</summary>
    Imported,

    /// <summary>Valheim's own <c>World_backup_auto-*</c> folder.</summary>
    GameAuto,
}

public sealed record BackupFile(string Name, long Size, string Sha256);

public sealed class BackupManifest
{
    public const string FileName = "vsm-backup.json";
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string WorldName { get; set; } = string.Empty;
    public int SaveNumber { get; set; }
    public BackupKind Kind { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTime? WorldSavedUtc { get; set; }
    public Guid? ProfileId { get; set; }
    public string? ProfileName { get; set; }
    public string? Note { get; set; }
    public long TotalZdos { get; set; }
    public int ChunkCount { get; set; }
    public bool CreativeKey { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public List<BackupFile> Files { get; set; } = [];

    [JsonIgnore]
    public long TotalBytes => Files.Sum(f => f.Size);
}

/// <summary>A backup as listed in the UI.</summary>
public sealed record BackupEntry(
    string Directory,
    string WorldName,
    int? SaveNumber,
    BackupKind Kind,
    DateTimeOffset CreatedAt,
    long TotalBytes,
    BackupManifest? Manifest)
{
    public string Name => Path.GetFileName(Directory);

    public bool HasManifest => Manifest is not null;

    public bool IsAutomatic => Kind is BackupKind.PreStart or BackupKind.PostStop;
}

public sealed record BackupVerification(bool Ok, IReadOnlyList<string> Problems);

public sealed record RestoreResult(BackupEntry Restored, BackupEntry? SafetyCopy, string? ReplacedFolder);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackupManifest))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
