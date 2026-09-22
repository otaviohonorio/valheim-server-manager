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

    /// <summary>
    /// Compares folders by the path Windows really opens, so a junction, symbolic link, subst drive
    /// or 8.3 name pointing at the game's folder is still recognised as the game's folder.
    /// </summary>
    public static bool SameDirectory(string a, string b) =>
        string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsInside(string path, string parent)
    {
        var p = Canonical(path) + Path.DirectorySeparatorChar;
        var root = Canonical(parent) + Path.DirectorySeparatorChar;
        return p.StartsWith(root, StringComparison.OrdinalIgnoreCase) && p.Length > root.Length;
    }

    private static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(FinalPath.Resolve(NormalizeDirectory(path)));

    /// <summary>
    /// The folder this program was installed to (where ValheimServerManager.exe lives; the CLI sits in
    /// its "cli" subfolder), or null when it cannot be told. Updates and uninstalls rewrite it.
    /// </summary>
    public static string? AppInstallDirectory
    {
        get
        {
            var dir = NormalizeDirectory(AppContext.BaseDirectory);
            foreach (var candidate in new[] { dir, Path.GetDirectoryName(dir) })
            {
                if (candidate is not null && File.Exists(Path.Combine(candidate, "ValheimServerManager.exe")))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Name of the sync service that owns <paramref name="path"/> (OneDrive, Dropbox, Google Drive,
    /// iCloud), or null. Sync clients lock and swap files while the server writes a save, and can leave
    /// older saves "online only".
    /// </summary>
    public static string? CloudSyncProvider(string path, IEnumerable<string>? oneDriveRoots = null)
    {
        oneDriveRoots ??= new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(System.Environment.GetEnvironmentVariable)
            .OfType<string>()
            .Where(Path.IsPathFullyQualified);
        if (oneDriveRoots.Any(root => SameDirectory(path, root) || IsInside(path, root)))
        {
            return "OneDrive";
        }

        foreach (var segment in NormalizeDirectory(path).Split(Path.DirectorySeparatorChar))
        {
            if (segment.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase))
            {
                return "OneDrive";
            }

            if (segment.Equals("Dropbox", StringComparison.OrdinalIgnoreCase) || segment.StartsWith("Dropbox (", StringComparison.OrdinalIgnoreCase))
            {
                return "Dropbox";
            }

            if (segment.Equals("Google Drive", StringComparison.OrdinalIgnoreCase) || segment.Equals("My Drive", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("Meu Drive", StringComparison.OrdinalIgnoreCase))
            {
                return "Google Drive";
            }

            if (segment.Equals("iCloudDrive", StringComparison.OrdinalIgnoreCase) || segment.Equals("iCloud Drive", StringComparison.OrdinalIgnoreCase))
            {
                return "iCloud";
            }
        }

        return null;
    }

    /// <summary>
    /// Where new servers go by default: Documents, unless Documents is synced to the cloud (Windows 11
    /// often moves it into OneDrive) — then the user folder, which is never synced.
    /// </summary>
    public static string DefaultServersRoot
    {
        get
        {
            var documents = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
            var root = string.IsNullOrEmpty(documents) || CloudSyncProvider(documents) is not null
                ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
                : documents;
            return Path.Combine(root, "Valheim Servers");
        }
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
