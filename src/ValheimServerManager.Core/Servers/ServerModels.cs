namespace ValheimServerManager.Core.Servers;

public enum ServerRunState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Crashed,
}

public enum CheckLevel
{
    Info,

    /// <summary>Start allowed only after the user confirms.</summary>
    Confirm,

    /// <summary>Start refused.</summary>
    Blocker,
}

public sealed record StartCheckItem(CheckLevel Level, string Code, string Message);

public sealed record StartOptions
{
    /// <summary>User agreed that a brand new world will be generated.</summary>
    public bool AllowNewWorld { get; init; }

    /// <summary>User read and accepted the warnings.</summary>
    public bool AcceptWarnings { get; init; }

    public static StartOptions Default { get; } = new();

    public static StartOptions Confirmed { get; } = new() { AllowNewWorld = true, AcceptWarnings = true };
}

public sealed record OperationResult(bool Success, string Message, IReadOnlyList<StartCheckItem> Checks)
{
    public bool NeedsConfirmation =>
        !Success && Checks.Any(c => c.Level == CheckLevel.Confirm) && Checks.All(c => c.Level != CheckLevel.Blocker);

    public static OperationResult Ok(string message) => new(true, message, []);

    public static OperationResult Fail(string message, IReadOnlyList<StartCheckItem>? checks = null) =>
        new(false, message, checks ?? []);
}

public sealed record ExitInfo(DateTimeOffset At, int? ExitCode, bool Clean, string Reason);

public enum AlertLevel
{
    Info,
    Success,
    Warning,
    Error,
    Critical,
}

public sealed record ServerAlert(AlertLevel Level, string Title, string Message, DateTimeOffset At)
{
    /// <summary>A player joined or left: shown as a notification only when the user asked for those.</summary>
    public bool IsPlayerNews { get; init; }
}

public enum ActivityKind
{
    Lifecycle,
    Player,
    Save,
    Backup,
    Warning,
    Error,
}

/// <summary>A character currently in the world.</summary>
/// <param name="Name">Character name.</param>
/// <param name="OwnerId">The peer id the server logs for the character (links join and leave lines).</param>
/// <param name="PlatformId">Steam_… or another platform id, when the log showed it.</param>
/// <param name="Since">When the character entered the world.</param>
public sealed record OnlinePlayer(string Name, long OwnerId, string? PlatformId, DateTimeOffset Since)
{
    public string? SteamId =>
        PlatformId?.StartsWith("Steam_", StringComparison.Ordinal) == true ? PlatformId["Steam_".Length..] : null;
}

public sealed record ServerActivity(DateTimeOffset At, ActivityKind Kind, string Message);

public sealed record ServerLogLine(DateTimeOffset ReceivedAt, string Text, bool Important);

/// <summary>Immutable snapshot of a server for the UI.</summary>
public sealed record ServerStatus
{
    public ServerRunState State { get; init; } = ServerRunState.Stopped;
    public int? ProcessId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public bool IsAttached { get; init; }
    public bool HasLog { get; init; } = true;
    public bool CreativeActive { get; init; }
    public string? JoinCode { get; init; }
    public string? PublicAddress { get; init; }
    public int PlayerCount { get; init; }

    /// <summary>Who is in the world, from the log. May lag <see cref="PlayerCount"/> while someone is loading.</summary>
    public IReadOnlyList<OnlinePlayer> OnlinePlayers { get; init; } = [];
    public int? LoadedSaveNumber { get; init; }
    public long? LoadedZdos { get; init; }
    public int? LastSaveNumber { get; init; }
    public DateTimeOffset? LastSaveAt { get; init; }
    public long? LastSaveMilliseconds { get; init; }
    public DateTimeOffset? LastBackupAt { get; init; }
    public string? LastBackupName { get; init; }
    public DateTimeOffset? StopRequestedAt { get; init; }
    public bool StopTimedOut { get; init; }
    public ExitInfo? LastExit { get; init; }
    public string? Emergency { get; init; }

    /// <summary>After a save, whether the world's <c>nobuildcost</c> key matched the launched mode.</summary>
    public bool? ModeVerified { get; init; }

    /// <summary>
    /// Differences between the profile and what the running server really uses (its command line and
    /// the modifiers it logged). Null until checked; empty when everything matches.
    /// </summary>
    public IReadOnlyList<Profiles.ConfigDifference>? ConfigDifferences { get; init; }

    /// <summary>How many settings were compared in the last check.</summary>
    public int ConfigCheckedCount { get; init; }

    public DateTimeOffset? ConfigCheckedAt { get; init; }

    public bool IsActive => State is ServerRunState.Starting or ServerRunState.Running or ServerRunState.Stopping;
}
