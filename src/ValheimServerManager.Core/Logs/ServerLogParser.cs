using System.Globalization;
using System.Text.RegularExpressions;

namespace ValheimServerManager.Core.Logs;

public enum ServerLogEventKind
{
    WorldLoadRequested,
    MissingWorldData,
    WorldLoading,
    WorldLoaded,
    ModifierApplied,
    ServerOnline,
    JoinCode,
    PublicAddress,
    PlayerCount,
    PlayerSpawned,
    SaveStarted,
    SaveCompleted,
    OrphanRemoved,
    ShutdownComplete,
    Error,
}

/// <summary>A meaningful line of <c>server.log</c>.</summary>
public sealed record ServerLogEvent(ServerLogEventKind Kind, DateTime? Timestamp, string Line)
{
    public int? Number { get; init; }
    public string? Text { get; init; }
    public long? Count { get; init; }
}

/// <summary>
/// Turns valheim_server log lines into events. Patterns were taken from real 1.0.12 logs.
/// </summary>
public static partial class ServerLogParser
{
    [GeneratedRegex(@"^(?<ts>\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2}): (?<msg>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"ZNet\.LoadWorld: (?<world>.+?) \(.+?\), save number (?<n>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LoadWorldRegex();

    [GeneratedRegex(@"^\s*missing (?<path>.+_main\.(?<n>\d+)\.db2)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MissingDb2Regex();

    [GeneratedRegex(@"ZDOMan\.LoadChunks - Starting to load (?<zdos>[\d.,]+) zdos from (?<chunks>\d+) Chunks", RegexOptions.CultureInvariant)]
    private static partial Regex LoadingRegex();

    [GeneratedRegex(@"Setting world modifier(?: preset)?: (?<v>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ModifierRegex();

    [GeneratedRegex(@"registered with join code (?<code>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex JoinCodeRegex();

    [GeneratedRegex(@"with join code \d+ and IP (?<ip>[\w.:\[\]-]+) is active", RegexOptions.CultureInvariant)]
    private static partial Regex AddressRegex();

    [GeneratedRegex(@"(?:now|is active with) (?<n>\d+) player\(s\)", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerCountRegex();

    [GeneratedRegex(@"Got character ZDOID from (?<name>.+?) :", RegexOptions.CultureInvariant)]
    private static partial Regex PlayerSpawnedRegex();

    [GeneratedRegex(@"World save \(1/5\).*=> Save number (?<n>\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SaveStartedRegex();

    [GeneratedRegex(@"World save \(5/5\) done\. Total time \[(?<ms>\d+)ms\]", RegexOptions.CultureInvariant)]
    private static partial Regex SaveCompletedRegex();

    [GeneratedRegex(@"Removing orphan (?:save|CHUNK) file: (?<path>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex OrphanRegex();

    public static ServerLogEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        DateTime? ts = null;
        var message = line;
        var stamped = TimestampRegex().Match(line);
        if (stamped.Success)
        {
            message = stamped.Groups["msg"].Value;
            if (DateTime.TryParseExact(stamped.Groups["ts"].Value, "MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                ts = parsed;
            }
        }

        Match m;
        if ((m = LoadWorldRegex().Match(message)).Success)
        {
            return new(ServerLogEventKind.WorldLoadRequested, ts, line) { Number = Int(m, "n"), Text = m.Groups["world"].Value };
        }

        if ((m = MissingDb2Regex().Match(message)).Success)
        {
            return new(ServerLogEventKind.MissingWorldData, ts, line) { Number = Int(m, "n"), Text = m.Groups["path"].Value };
        }

        if ((m = LoadingRegex().Match(message)).Success)
        {
            var digits = new string(m.Groups["zdos"].Value.Where(char.IsDigit).ToArray());
            return new(ServerLogEventKind.WorldLoading, ts, line)
            {
                Count = long.Parse(digits, CultureInfo.InvariantCulture),
                Number = Int(m, "chunks"),
            };
        }

        if (message.StartsWith("ZDOMan.LoadChunks done", StringComparison.Ordinal))
        {
            return new(ServerLogEventKind.WorldLoaded, ts, line);
        }

        if ((m = ModifierRegex().Match(message)).Success)
        {
            return new(ServerLogEventKind.ModifierApplied, ts, line) { Text = m.Groups["v"].Value.Trim() };
        }

        if (message == "Game server connected")
        {
            return new(ServerLogEventKind.ServerOnline, ts, line);
        }

        if ((m = JoinCodeRegex().Match(message)).Success)
        {
            return new(ServerLogEventKind.JoinCode, ts, line) { Text = m.Groups["code"].Value };
        }

        if ((m = SaveStartedRegex().Match(message)).Success)
        {
            return new(ServerLogEventKind.SaveStarted, ts, line) { Number = Int(m, "n") };
        }

        if ((m = SaveCompletedRegex().Match(message)).Success)
        {
            return new(ServerLogEventKind.SaveCompleted, ts, line) { Count = Int(m, "ms") };
        }

        if ((m = PlayerCountRegex().Match(message)).Success)
        {
            var address = AddressRegex().Match(message);
            return new(ServerLogEventKind.PlayerCount, ts, line)
            {
                Number = Int(m, "n"),
                Text = address.Success ? address.Groups["ip"].Value : null,
            };
        }

        if ((m = PlayerSpawnedRegex().Match(message)).Success)
        {
            return new(ServerLogEventKind.PlayerSpawned, ts, line) { Text = m.Groups["name"].Value };
        }

        if ((m = OrphanRegex().Match(message)).Success)
        {
            return new(ServerLogEventKind.OrphanRemoved, ts, line) { Text = m.Groups["path"].Value };
        }

        if (message is "Net scene destroyed" or "ZNet OnDestroy")
        {
            return new(ServerLogEventKind.ShutdownComplete, ts, line);
        }

        if (message.Contains("Exception", StringComparison.Ordinal) && !message.Contains("Handled", StringComparison.Ordinal))
        {
            return new(ServerLogEventKind.Error, ts, line) { Text = message };
        }

        return null;
    }

    private static int Int(Match m, string group) =>
        int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
}
