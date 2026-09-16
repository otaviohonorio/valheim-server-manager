using System.Text.Json.Serialization;

namespace ValheimServerManager.Core.Profiles;

/// <summary>Everything needed to launch one dedicated server world.</summary>
public sealed class ServerProfile
{
    public const int DefaultPort = 2456;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = "Meu servidor";

    // Locations
    public string ServerDirectory { get; set; } = string.Empty;
    public string SaveDirectory { get; set; } = string.Empty;

    /// <summary>Empty means <c>&lt;parent of SaveDirectory&gt;\backups</c>.</summary>
    public string BackupDirectory { get; set; } = string.Empty;

    // Identity
    public string ServerName { get; set; } = string.Empty;
    public string WorldName { get; set; } = string.Empty;
    public int Port { get; set; } = DefaultPort;

    /// <summary>Plain text in memory only. Persisted encrypted (DPAPI) by the settings store.</summary>
    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public bool Public { get; set; } = true;
    public bool Crossplay { get; set; }

    // World rules
    public WorldPreset Preset { get; set; } = WorldPreset.Normal;
    public CombatLevel Combat { get; set; } = CombatLevel.Default;
    public DeathPenaltyLevel DeathPenalty { get; set; } = DeathPenaltyLevel.Default;
    public ResourceRate Resources { get; set; } = ResourceRate.Default;
    public RaidFrequency Raids { get; set; } = RaidFrequency.Default;
    public PortalRule Portals { get; set; } = PortalRule.Default;
    public bool PlayerEvents { get; set; }
    public bool PassiveMobs { get; set; }
    public bool NoMap { get; set; }

    /// <summary>Adds <c>-setkey nobuildcost</c>: free building and crafting for everyone, no achievements.</summary>
    public bool CreativeMode { get; set; }

    // Valheim's own save cadence
    public int SaveIntervalSeconds { get; set; } = 1800;
    public int GameBackupCount { get; set; } = 4;
    public int GameBackupShortSeconds { get; set; } = 7200;
    public int GameBackupLongSeconds { get; set; } = 43200;

    // Manager safety net
    public bool BackupBeforeStart { get; set; } = true;
    public bool BackupAfterStop { get; set; } = true;
    public int AutoBackupRetention { get; set; } = 20;
    public int StopTimeoutSeconds { get; set; } = 120;

    /// <summary>Advanced: appended verbatim. Must not repeat arguments the manager controls.</summary>
    public string ExtraArguments { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsCreativeEffective => CreativeMode || Preset == WorldPreset.Hammer;

    [JsonIgnore]
    public string EffectiveBackupDirectory =>
        !string.IsNullOrWhiteSpace(BackupDirectory)
            ? BackupDirectory
            : Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(SaveDirectory)) ?? SaveDirectory, "backups");

    [JsonIgnore]
    public string LogFilePath => Path.Combine(SaveDirectory, "server.log");

    [JsonIgnore]
    public string ArchivedLogsDirectory => Path.Combine(SaveDirectory, "logs");

    [JsonIgnore]
    public string ServerExecutable => Path.Combine(ServerDirectory, "valheim_server.exe");

    public ServerProfile Clone()
    {
        var copy = (ServerProfile)MemberwiseClone();
        return copy;
    }

    public ServerProfile Duplicate(string displayName)
    {
        var copy = Clone();
        copy.Id = Guid.NewGuid();
        copy.DisplayName = displayName;
        return copy;
    }
}
