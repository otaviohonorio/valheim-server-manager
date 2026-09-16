using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ValheimServerManager.Core.Platform;

/// <summary>Well-known Valheim and Steam locations on this machine.</summary>
[SupportedOSPlatform("windows")]
public static partial class ValheimPaths
{
    public const int GameAppId = 892970;
    public const int DedicatedServerAppId = 896660;
    public const string ServerExecutableName = "valheim_server.exe";
    public const string ServerProcessName = "valheim_server";
    public const string GameProcessName = "valheim";

    /// <summary>
    /// The folder the game client (and a server launched without <c>-savedir</c>) uses:
    /// <c>%USERPROFILE%\AppData\LocalLow\IronGate\Valheim</c>.
    /// </summary>
    public static string GameDataDirectory =>
        Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            "AppData", "LocalLow", "IronGate", "Valheim");

    public static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool SameDirectory(string a, string b) =>
        string.Equals(NormalizeDirectory(a), NormalizeDirectory(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsInside(string path, string parent)
    {
        var p = NormalizeDirectory(path) + Path.DirectorySeparatorChar;
        var root = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        return p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && p.Length > root.Length;
    }

    public static string? FindSteamDirectory()
    {
        foreach (var (hive, key) in new[]
                 {
                     (Registry.CurrentUser, @"Software\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
                     (Registry.LocalMachine, @"SOFTWARE\Valve\Steam"),
                 })
        {
            using var k = hive.OpenSubKey(key);
            var value = (k?.GetValue("SteamPath") ?? k?.GetValue("InstallPath")) as string;
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                return Path.GetFullPath(value);
            }
        }

        var fallback = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public static IReadOnlyList<string> FindSteamLibraries()
    {
        var steam = FindSteamDirectory();
        if (steam is null)
        {
            return [];
        }

        var libraries = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
            {
                var path = m.Groups["path"].Value.Replace(@"\\", @"\", StringComparison.Ordinal);
                if (Directory.Exists(path))
                {
                    libraries.Add(Path.GetFullPath(path));
                }
            }
        }

        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Finds installed copies of the "Valheim Dedicated Server" Steam tool.</summary>
    public static IReadOnlyList<string> FindDedicatedServerInstallations()
    {
        var found = new List<string>();
        foreach (var library in FindSteamLibraries())
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{DedicatedServerAppId}.acf");
            string? installDir = null;
            if (File.Exists(manifest))
            {
                var m = InstallDirRegex().Match(File.ReadAllText(manifest));
                if (m.Success)
                {
                    installDir = Path.Combine(library, "steamapps", "common", m.Groups["dir"].Value);
                }
            }

            installDir ??= Path.Combine(library, "steamapps", "common", "Valheim dedicated server");
            if (File.Exists(Path.Combine(installDir, ServerExecutableName)))
            {
                found.Add(installDir);
            }
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [GeneratedRegex("\"path\"\\s+\"(?<path>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex LibraryPathRegex();

    [GeneratedRegex("\"installdir\"\\s+\"(?<dir>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex InstallDirRegex();
}
