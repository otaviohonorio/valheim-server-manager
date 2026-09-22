using System.Globalization;
using ValheimServerManager.Core.Localization;

namespace ValheimServerManager.Core.Profiles;

/// <summary>A setting whose value in the running server differs from the profile.</summary>
public sealed record ConfigDifference(string Setting, string Expected, string Actual);

/// <summary>
/// Compares what a running valheim_server actually received (its real command line, and the
/// modifiers it logged) with what the profile says. Password values are never echoed.
/// </summary>
public static class LaunchVerification
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-name"] = Strings.Verify_ServerName,
        ["-port"] = Strings.Verify_Port,
        ["-world"] = Strings.Verify_World,
        ["-password"] = Strings.Verify_Password,
        ["-public"] = Strings.Verify_Public,
        ["-crossplay"] = Strings.Verify_Crossplay,
        ["-savedir"] = Strings.Verify_SaveDir,
        ["-logfile"] = Strings.Verify_LogFile,
        ["-saveinterval"] = Strings.Verify_SaveInterval,
        ["-backups"] = Strings.Verify_GameBackups,
        ["-backupshort"] = Strings.Verify_GameBackupShort,
        ["-backuplong"] = Strings.Verify_GameBackupLong,
        ["-preset"] = Strings.Verify_Preset,
    };

    /// <summary>Values Valheim uses when the argument is absent.</summary>
    private static readonly Dictionary<string, string> GameDefaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["-saveinterval"] = "1800",
        ["-backups"] = "4",
        ["-backupshort"] = "7200",
        ["-backuplong"] = "43200",
    };

    private static readonly HashSet<string> PathOptions = new(StringComparer.OrdinalIgnoreCase) { "-savedir", "-logfile" };

    private static string Absent => Strings.Verify_Absent;

    public static int CheckedSettingsCount(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var parsed = ParsedArgs.From(LaunchArguments.Build(profile));
        return Labels.Count + parsed.Modifiers.Count + parsed.Keys.Count + (parsed.Extras.Count > 0 ? 1 : 0);
    }

    public static IReadOnlyList<ConfigDifference> Compare(ServerProfile profile, string runningCommandLine)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var expected = ParsedArgs.From(LaunchArguments.Build(profile));
        var tokens = CommandLine.Split(runningCommandLine);
        var actual = ParsedArgs.From(tokens.Count > 0 && !tokens[0].StartsWith('-') ? tokens.Skip(1).ToArray() : tokens);
        var diffs = new List<ConfigDifference>();

        foreach (var (option, label) in Labels)
        {
            if (option.Equals("-crossplay", StringComparison.OrdinalIgnoreCase))
            {
                if (expected.Flags.Contains(option) != actual.Flags.Contains(option))
                {
                    diffs.Add(new(label, OnOff(expected.Flags.Contains(option)), OnOff(actual.Flags.Contains(option))));
                }

                continue;
            }

            var want = expected.Values.GetValueOrDefault(option) ?? GameDefaults.GetValueOrDefault(option) ?? Absent;
            var have = actual.Values.GetValueOrDefault(option) ?? GameDefaults.GetValueOrDefault(option) ?? Absent;
            if (Same(option, want, have))
            {
                continue;
            }

            diffs.Add(option.Equals("-password", StringComparison.OrdinalIgnoreCase)
                ? new(label, Strings.Verify_ProfilePassword, have == Absent ? Absent : Strings.Verify_OtherPassword)
                : new(label, Describe(option, want), Describe(option, have)));
        }

        foreach (var name in expected.Modifiers.Keys.Union(actual.Modifiers.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var want = expected.Modifiers.GetValueOrDefault(name) ?? "default";
            var have = actual.Modifiers.GetValueOrDefault(name) ?? "default";
            if (!want.Equals(have, StringComparison.OrdinalIgnoreCase))
            {
                diffs.Add(new(string.Format(CultureInfo.CurrentCulture, Strings.Verify_Modifier, name), want, have));
            }
        }

        foreach (var key in expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var want = expected.Keys.Contains(key);
            var have = actual.Keys.Contains(key);
            if (want != have)
            {
                diffs.Add(new(string.Format(CultureInfo.CurrentCulture, Strings.Verify_Key, key), OnOff(want), OnOff(have)));
            }
        }

        if (!expected.Extras.SequenceEqual(actual.Extras, StringComparer.Ordinal))
        {
            diffs.Add(new(Strings.Verify_ExtraArguments, Join(expected.Extras), Join(actual.Extras)));
        }

        return diffs;
    }

    /// <summary>
    /// Compares the modifier lines from <c>server.log</c> ("Setting world modifier: deathpenalty-&gt;casual",
    /// "Setting world modifier preset: normal") with the profile.
    /// </summary>
    public static IReadOnlyList<ConfigDifference> CompareLoggedModifiers(ServerProfile profile, IReadOnlyCollection<string> loggedLines)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(loggedLines);
        var expected = ParsedArgs.From(LaunchArguments.Build(profile));
        var logged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? preset = null;
        foreach (var line in loggedLines)
        {
            if (!line.Contains("->", StringComparison.Ordinal))
            {
                preset = line.Trim();
                continue;
            }

            var parts = line.Split("->", 2);
            logged[parts[0].Trim()] = parts[1].Trim();
        }

        var diffs = new List<ConfigDifference>();
        var wantPreset = expected.Values.GetValueOrDefault("-preset") ?? Absent;
        if (!wantPreset.Equals(preset ?? Absent, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new(Strings.Verify_PresetApplied, wantPreset, preset ?? Strings.Verify_NoneInLog));
        }

        foreach (var (name, value) in expected.Modifiers)
        {
            var have = logged.GetValueOrDefault(name) ?? Strings.Verify_NotApplied;
            if (!value.Equals(have, StringComparison.OrdinalIgnoreCase))
            {
                diffs.Add(new(string.Format(CultureInfo.CurrentCulture, Strings.Verify_ModifierApplied, name), value, have));
            }
        }

        foreach (var (name, value) in logged.Where(kv => !expected.Modifiers.ContainsKey(kv.Key)))
        {
            diffs.Add(new(string.Format(CultureInfo.CurrentCulture, Strings.Verify_ModifierApplied, name), "default", value));
        }

        return diffs;
    }

    private static bool Same(string option, string a, string b) =>
        PathOptions.Contains(option) && a != Absent && b != Absent
            ? SamePath(a, b)
            : string.Equals(a, b, option.Equals("-world", StringComparison.OrdinalIgnoreCase) || option.Equals("-password", StringComparison.OrdinalIgnoreCase) || option.Equals("-name", StringComparison.OrdinalIgnoreCase)
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string Describe(string option, string value) => option.ToLowerInvariant() switch
    {
        _ when value == Absent => value,
        "-public" => value == "1" ? Strings.Verify_Yes : Strings.Verify_No,
        "-saveinterval" or "-backupshort" or "-backuplong" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            => s % 3600 == 0 ? $"{s / 3600} h" : s % 60 == 0 ? $"{s / 60} min" : $"{s} s",
        _ => value,
    };

    private static string OnOff(bool on) => on ? Strings.Verify_On : Strings.Verify_Off;

    private static string Join(IReadOnlyList<string> args) => args.Count == 0 ? Strings.Verify_None : CommandLine.Join(args);

    private sealed class ParsedArgs
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Modifiers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Extras { get; } = [];

        public static ParsedArgs From(IReadOnlyList<string> args)
        {
            var parsed = new ParsedArgs();
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                string Next() => i + 1 < args.Count ? args[++i] : string.Empty;
                switch (arg.ToLowerInvariant())
                {
                    case "-nographics":
                    case "-batchmode":
                        break;
                    case "-crossplay":
                        parsed.Flags.Add(arg);
                        break;
                    case "-modifier":
                        var name = Next();
                        parsed.Modifiers[name] = Next();
                        break;
                    case "-setkey":
                        parsed.Keys.Add(Next());
                        break;
                    default:
                        if (Labels.ContainsKey(arg))
                        {
                            parsed.Values[arg.ToLowerInvariant()] = Next();
                        }
                        else
                        {
                            parsed.Extras.Add(arg);
                        }

                        break;
                }
            }

            return parsed;
        }
    }
}
