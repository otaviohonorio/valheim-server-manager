using System.Globalization;
using System.Text.RegularExpressions;

namespace ValheimServerManager.Core.Profiles;

public sealed record BatchImportResult(ServerProfile Profile, IReadOnlyList<string> Notes);

/// <summary>
/// Reads a <c>start_headless_server.bat</c>-style script and turns its <c>valheim_server</c>
/// line into a profile. Handles <c>set "VAR=value"</c> / <c>set VAR=value</c> and <c>%VAR%</c>.
/// </summary>
public static partial class BatchFileImporter
{
    [GeneratedRegex(@"^\s*set\s+(?:""(?<name>[^=""]+)=(?<value>[^""]*)""|(?<name>[^=\s]+)=(?<value>.*))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetRegex();

    [GeneratedRegex(@"%(?<name>[^%]+)%", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"^\s*(?:""[^""]*valheim_server(?:\.exe)?""|\S*valheim_server(?:\.exe)?)\s+(?<args>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerLineRegex();

    public static BatchImportResult ImportFile(string batchPath)
    {
        var profile = Import(File.ReadAllLines(batchPath), out var notes);
        profile.DisplayName = Path.GetFileNameWithoutExtension(batchPath);
        return new BatchImportResult(profile, notes);
    }

    public static ServerProfile Import(IEnumerable<string> lines, out IReadOnlyList<string> notes)
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();
        var profile = new ServerProfile();
        string? currentDirectory = null;
        var found = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("REM", StringComparison.OrdinalIgnoreCase) || line.StartsWith("::", StringComparison.Ordinal))
            {
                continue;
            }

            var set = SetRegex().Match(line);
            if (set.Success)
            {
                variables[set.Groups["name"].Value.Trim()] = Expand(set.Groups["value"].Value, variables);
                continue;
            }

            if (line.StartsWith("cd ", StringComparison.OrdinalIgnoreCase))
            {
                currentDirectory = Expand(line[3..].Replace("/d", string.Empty, StringComparison.OrdinalIgnoreCase).Trim().Trim('"'), variables);
                continue;
            }

            var server = ServerLineRegex().Match(line);
            if (!server.Success)
            {
                continue;
            }

            found = true;
            var args = CommandLine.Split(Expand(server.Groups["args"].Value, variables));
            Apply(profile, args, messages);
        }

        if (!found)
        {
            throw new InvalidDataException("Não encontrei uma linha que execute valheim_server no arquivo.");
        }

        if (string.IsNullOrWhiteSpace(profile.ServerDirectory))
        {
            if (variables.TryGetValue("SERVERDIR", out var serverDir))
            {
                profile.ServerDirectory = serverDir;
            }
            else if (currentDirectory is not null && !currentDirectory.Contains('%', StringComparison.Ordinal))
            {
                profile.ServerDirectory = currentDirectory;
            }
        }

        if (string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            messages.Add("O .bat não usa -savedir: o servidor estava gravando na pasta do próprio jogo. Escolha uma pasta só do servidor antes de iniciar.");
        }

        notes = messages;
        return profile;
    }

    private static string Expand(string value, IReadOnlyDictionary<string, string> variables) =>
        VariableRegex().Replace(value, m => variables.TryGetValue(m.Groups["name"].Value, out var v) ? v : m.Value);

    private static void Apply(ServerProfile profile, IReadOnlyList<string> args, List<string> notes)
    {
        var extras = new List<string>();
        profile.Public = false;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string Next() => i + 1 < args.Count ? args[++i] : string.Empty;

            switch (arg.ToLowerInvariant())
            {
                case "-nographics":
                case "-batchmode":
                    break;
                case "-name":
                    profile.ServerName = Next();
                    break;
                case "-port":
                    if (int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
                    {
                        profile.Port = port;
                    }

                    break;
                case "-world":
                    profile.WorldName = Next();
                    break;
                case "-password":
                    profile.Password = Next();
                    break;
                case "-public":
                    profile.Public = Next() == "1";
                    break;
                case "-crossplay":
                    profile.Crossplay = true;
                    break;
                case "-savedir":
                    profile.SaveDirectory = Next();
                    break;
                case "-logfile":
                    _ = Next();
                    break;
                case "-saveinterval":
                    profile.SaveIntervalSeconds = ParseInt(Next(), profile.SaveIntervalSeconds);
                    break;
                case "-backups":
                    profile.GameBackupCount = ParseInt(Next(), profile.GameBackupCount);
                    break;
                case "-backupshort":
                    profile.GameBackupShortSeconds = ParseInt(Next(), profile.GameBackupShortSeconds);
                    break;
                case "-backuplong":
                    profile.GameBackupLongSeconds = ParseInt(Next(), profile.GameBackupLongSeconds);
                    break;
                case "-preset":
                    var preset = Next();
                    if (ModifierCatalog.TryParse(ModifierCatalog.Presets, preset, out WorldPreset p))
                    {
                        profile.Preset = p;
                    }
                    else
                    {
                        notes.Add($"Preset desconhecido ignorado: {preset}");
                    }

                    break;
                case "-modifier":
                    ApplyModifier(profile, Next(), Next(), notes);
                    break;
                case "-setkey":
                    ApplyKey(profile, Next(), notes);
                    break;
                default:
                    extras.Add(arg);
                    break;
            }
        }

        profile.ExtraArguments = CommandLine.Join(extras);
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static void ApplyModifier(ServerProfile profile, string name, string value, List<string> notes)
    {
        var ok = name.ToLowerInvariant() switch
        {
            "combat" => TrySet(ModifierCatalog.Combat, value, v => profile.Combat = v),
            "deathpenalty" => TrySet(ModifierCatalog.DeathPenalty, value, v => profile.DeathPenalty = v),
            "resources" => TrySet(ModifierCatalog.Resources, value, v => profile.Resources = v),
            "raids" => TrySet(ModifierCatalog.Raids, value, v => profile.Raids = v),
            "portals" => TrySet(ModifierCatalog.Portals, value, v => profile.Portals = v),
            _ => false,
        };

        if (!ok)
        {
            notes.Add($"Modificador ignorado: {name} {value}");
        }
    }

    private static bool TrySet<T>(IReadOnlyList<ModifierOption<T>> options, string token, Action<T> apply)
        where T : struct, Enum
    {
        if (!ModifierCatalog.TryParse(options, token, out T value))
        {
            return false;
        }

        apply(value);
        return true;
    }

    private static void ApplyKey(ServerProfile profile, string key, List<string> notes)
    {
        switch (key.ToLowerInvariant())
        {
            case WorldKeys.NoBuildCost:
                profile.CreativeMode = true;
                break;
            case WorldKeys.PlayerEvents:
                profile.PlayerEvents = true;
                break;
            case WorldKeys.PassiveMobs:
                profile.PassiveMobs = true;
                break;
            case WorldKeys.NoMap:
                profile.NoMap = true;
                break;
            default:
                notes.Add($"Chave desconhecida ignorada: {key}");
                break;
        }
    }
}
