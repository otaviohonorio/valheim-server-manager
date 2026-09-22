using System.Globalization;
using System.Runtime.Versioning;
using ValheimServerManager.Core.Localization;
using ValheimServerManager.Core.Platform;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Profiles;

public enum ValidationSeverity
{
    Warning,
    Error,
}

public sealed record ValidationIssue(string Field, ValidationSeverity Severity, string Message);

/// <summary>Rules a profile must satisfy before the server may start.</summary>
[SupportedOSPlatform("windows")]
public static class ProfileValidator
{
    public const int MinPasswordLength = 5;

    public static IReadOnlyList<ValidationIssue> Validate(ServerProfile profile, string? gameDataDirectory = null, string? appInstallDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        gameDataDirectory ??= ValheimPaths.GameDataDirectory;
        appInstallDirectory ??= ValheimPaths.AppInstallDirectory;
        var issues = new List<ValidationIssue>();

        void Error(string field, string message) => issues.Add(new(field, ValidationSeverity.Error, message));
        void Warn(string field, string message) => issues.Add(new(field, ValidationSeverity.Warning, message));

        // Server install
        if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
        {
            Error(nameof(profile.ServerDirectory), Strings.Validator_ServerDirRequired);
        }
        else if (!File.Exists(profile.ServerExecutable))
        {
            Error(nameof(profile.ServerDirectory), string.Format(CultureInfo.CurrentCulture, Strings.Validator_ServerExeNotFound, ValheimPaths.ServerExecutableName, profile.ServerDirectory));
        }

        // Save directory — the rule that would have prevented the incident
        if (string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            Error(nameof(profile.SaveDirectory), Strings.Validator_SaveDirRequired);
        }
        else if (!Path.IsPathFullyQualified(profile.SaveDirectory))
        {
            Error(nameof(profile.SaveDirectory), Strings.Validator_SaveDirNotFullPath);
        }
        else if (ValheimPaths.SameDirectory(profile.SaveDirectory, gameDataDirectory))
        {
            Error(nameof(profile.SaveDirectory), Strings.Validator_SaveDirIsGameFolder);
        }
        else if (ValheimPaths.IsInside(profile.SaveDirectory, gameDataDirectory))
        {
            Warn(nameof(profile.SaveDirectory), Strings.Validator_SaveDirInsideGameFolder);
        }
        else if (appInstallDirectory is not null && IsSameOrInside(profile.SaveDirectory, appInstallDirectory))
        {
            Error(nameof(profile.SaveDirectory), Strings.Validator_SaveDirInsideAppFolder);
        }
        else if (ValheimPaths.CloudSyncProvider(profile.SaveDirectory) is { } cloud)
        {
            Warn(nameof(profile.SaveDirectory), string.Format(CultureInfo.CurrentCulture, Strings.Validator_SaveDirInCloud, cloud));
        }

        // Backups
        if (!string.IsNullOrWhiteSpace(profile.SaveDirectory) && Path.IsPathFullyQualified(profile.SaveDirectory))
        {
            var backups = profile.EffectiveBackupDirectory;
            var worlds = WorldFolder.WorldsDirectory(profile.SaveDirectory);
            if (ValheimPaths.SameDirectory(backups, worlds) || ValheimPaths.IsInside(backups, worlds))
            {
                Error(nameof(profile.BackupDirectory), Strings.Validator_BackupsInsideWorlds);
            }
            else if (appInstallDirectory is not null && IsSameOrInside(backups, appInstallDirectory))
            {
                Error(nameof(profile.BackupDirectory), Strings.Validator_BackupsInsideAppFolder);
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.BackupDirectory) && !Path.IsPathFullyQualified(profile.BackupDirectory))
        {
            Error(nameof(profile.BackupDirectory), Strings.Validator_BackupDirNotFullPath);
        }

        // Identity
        if (string.IsNullOrWhiteSpace(profile.ServerName))
        {
            Error(nameof(profile.ServerName), Strings.Validator_ServerNameRequired);
        }
        else if (profile.ServerName.Contains('"'))
        {
            Error(nameof(profile.ServerName), Strings.Validator_ServerNameQuotes);
        }

        if (string.IsNullOrWhiteSpace(profile.WorldName))
        {
            Error(nameof(profile.WorldName), Strings.Validator_WorldNameRequired);
        }
        else if (profile.WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || profile.WorldName.Contains('"'))
        {
            Error(nameof(profile.WorldName), Strings.Validator_WorldNameInvalidChars);
        }
        else if (profile.WorldName.Contains(WorldFolder.GameAutoBackupMarker, StringComparison.OrdinalIgnoreCase))
        {
            Error(nameof(profile.WorldName), Strings.Validator_WorldNameIsGameBackup);
        }

        if (profile.Port is < 1024 or > 65534)
        {
            Error(nameof(profile.Port), Strings.Validator_PortRange);
        }

        // Password rules enforced by valheim_server itself
        if (string.IsNullOrEmpty(profile.Password))
        {
            Error(nameof(profile.Password), Strings.Validator_PasswordRequired);
        }
        else
        {
            if (profile.Password.Length < MinPasswordLength)
            {
                Error(nameof(profile.Password), string.Format(CultureInfo.CurrentCulture, Strings.Validator_PasswordTooShort, MinPasswordLength));
            }

            if (profile.Password.Contains('"'))
            {
                Error(nameof(profile.Password), Strings.Validator_PasswordQuotes);
            }

            if (!string.IsNullOrEmpty(profile.ServerName) &&
                profile.ServerName.Contains(profile.Password, StringComparison.OrdinalIgnoreCase))
            {
                Error(nameof(profile.Password), Strings.Validator_PasswordInServerName);
            }
        }

        // Save cadence
        if (profile.SaveIntervalSeconds < 60)
        {
            Error(nameof(profile.SaveIntervalSeconds), Strings.Validator_SaveIntervalTooShort);
        }
        else if (profile.SaveIntervalSeconds > 3600)
        {
            Warn(nameof(profile.SaveIntervalSeconds), Strings.Validator_SaveIntervalLong);
        }

        if (profile.GameBackupCount is < 1 or > 100)
        {
            Error(nameof(profile.GameBackupCount), Strings.Validator_GameBackupCountRange);
        }

        if (profile.StopTimeoutSeconds is < 15 or > 900)
        {
            Error(nameof(profile.StopTimeoutSeconds), Strings.Validator_StopTimeoutRange);
        }

        if (profile.AutoBackupRetention is < 1 or > 500)
        {
            Error(nameof(profile.AutoBackupRetention), Strings.Validator_RetentionRange);
        }

        if (!profile.BackupBeforeStart)
        {
            Warn(nameof(profile.BackupBeforeStart), Strings.Validator_NoBackupBeforeStart);
        }

        // Extra arguments cannot override managed ones
        foreach (var arg in CommandLine.Split(profile.ExtraArguments))
        {
            if (LaunchArguments.ManagedArguments.Contains(arg))
            {
                Error(nameof(profile.ExtraArguments), string.Format(CultureInfo.CurrentCulture, Strings.Validator_ManagedArgument, arg));
            }
        }

        if (profile.Preset == WorldPreset.Hammer && !profile.CreativeMode)
        {
            Warn(nameof(profile.Preset), Strings.Validator_HammerIsCreative);
        }

        return issues;
    }

    private static bool IsSameOrInside(string path, string parent) =>
        ValheimPaths.SameDirectory(path, parent) || ValheimPaths.IsInside(path, parent);

    public static bool HasErrors(this IEnumerable<ValidationIssue> issues) =>
        issues.Any(i => i.Severity == ValidationSeverity.Error);
}
