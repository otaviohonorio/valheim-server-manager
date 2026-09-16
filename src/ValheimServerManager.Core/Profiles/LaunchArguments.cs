using System.Globalization;
using System.Text;

namespace ValheimServerManager.Core.Profiles;

/// <summary>
/// Builds the <c>valheim_server.exe</c> argument list. Two rules come straight from the 2026-09-16
/// incident and are not optional:
/// <list type="bullet">
/// <item><c>-savedir</c> and <c>-logFile</c> are always passed, so the server never shares the
/// game's folder and always leaves a log behind.</item>
/// <item><c>-preset</c> is always passed and always first among world rules. With a preset on the
/// line, Valheim rebuilds world keys on every boot, so turning creative mode off really removes
/// <c>nobuildcost</c> from the world. Without it the key stays saved in the world forever.</item>
/// </list>
/// </summary>
public static class LaunchArguments
{
    /// <summary>Arguments owned by the manager; users may not override them via extra arguments.</summary>
    public static readonly IReadOnlySet<string> ManagedArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "-nographics", "-batchmode", "-name", "-port", "-world", "-password", "-public", "-crossplay",
        "-savedir", "-logfile", "-saveinterval", "-backups", "-backupshort", "-backuplong",
        "-preset", "-modifier", "-setkey",
    };

    public static IReadOnlyList<string> Build(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var args = new List<string>
        {
            "-nographics",
            "-batchmode",
            "-name", profile.ServerName,
            "-port", profile.Port.ToString(CultureInfo.InvariantCulture),
            "-world", profile.WorldName,
            "-password", profile.Password,
            "-public", profile.Public ? "1" : "0",
        };

        if (profile.Crossplay)
        {
            args.Add("-crossplay");
        }

        args.AddRange(
        [
            "-savedir", profile.SaveDirectory,
            "-logFile", profile.LogFilePath,
            "-saveinterval", profile.SaveIntervalSeconds.ToString(CultureInfo.InvariantCulture),
            "-backups", profile.GameBackupCount.ToString(CultureInfo.InvariantCulture),
            "-backupshort", profile.GameBackupShortSeconds.ToString(CultureInfo.InvariantCulture),
            "-backuplong", profile.GameBackupLongSeconds.ToString(CultureInfo.InvariantCulture),
            "-preset", ModifierCatalog.Token(ModifierCatalog.Presets, profile.Preset),
        ]);

        AddModifier(args, "combat", profile.Combat, ModifierCatalog.Combat, CombatLevel.Default);
        AddModifier(args, "deathpenalty", profile.DeathPenalty, ModifierCatalog.DeathPenalty, DeathPenaltyLevel.Default);
        AddModifier(args, "resources", profile.Resources, ModifierCatalog.Resources, ResourceRate.Default);
        AddModifier(args, "raids", profile.Raids, ModifierCatalog.Raids, RaidFrequency.Default);
        AddModifier(args, "portals", profile.Portals, ModifierCatalog.Portals, PortalRule.Default);

        if (profile.CreativeMode) AddKey(args, WorldKeys.NoBuildCost);
        if (profile.PlayerEvents) AddKey(args, WorldKeys.PlayerEvents);
        if (profile.PassiveMobs) AddKey(args, WorldKeys.PassiveMobs);
        if (profile.NoMap) AddKey(args, WorldKeys.NoMap);

        args.AddRange(CommandLine.Split(profile.ExtraArguments));
        return args;
    }

    /// <summary>Command line with the password masked, safe to show on screen or in logs.</summary>
    public static string BuildDisplay(ServerProfile profile)
    {
        var args = Build(profile).ToList();
        var passwordIndex = args.FindIndex(a => a.Equals("-password", StringComparison.OrdinalIgnoreCase));
        if (passwordIndex >= 0 && passwordIndex + 1 < args.Count)
        {
            args[passwordIndex + 1] = "••••••";
        }

        return "valheim_server.exe " + CommandLine.Join(args);
    }

    private static void AddModifier<T>(List<string> args, string name, T value, IReadOnlyList<ModifierOption<T>> options, T defaultValue)
        where T : struct, Enum
    {
        if (EqualityComparer<T>.Default.Equals(value, defaultValue))
        {
            return;
        }

        args.AddRange(["-modifier", name, ModifierCatalog.Token(options, value)]);
    }

    private static void AddKey(List<string> args, string key) => args.AddRange(["-setkey", key]);
}

/// <summary>Windows command line quoting compatible with <c>CommandLineToArgvW</c>.</summary>
public static class CommandLine
{
    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    public static string Quote(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }

            backslashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Splits a command line using the same rules as <c>CommandLineToArgvW</c> (program name rules excluded).</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        var i = 0;
        while (i < commandLine.Length)
        {
            var c = commandLine[i];
            if (c == '\\')
            {
                var start = i;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    i++;
                }

                var count = i - start;
                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', count);
                }

                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                    continue;
                }

                inQuotes = !inQuotes;
                hasToken = true;
                i++;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                i++;
                continue;
            }

            current.Append(c);
            hasToken = true;
            i++;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
