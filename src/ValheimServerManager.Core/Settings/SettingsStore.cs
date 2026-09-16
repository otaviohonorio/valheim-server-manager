using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ValheimServerManager.Core.Profiles;

namespace ValheimServerManager.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<ServerProfile> Profiles { get; set; } = [];
    public Guid? SelectedProfileId { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool NotifyPlayerJoins { get; set; } = true;
    public bool FirstRunCompleted { get; set; }

    /// <summary>DPAPI-protected passwords keyed by profile id (never plain text on disk).</summary>
    public Dictionary<Guid, string> ProtectedPasswords { get; set; } = [];
}

public interface ISettingsStore
{
    string DataDirectory { get; }

    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>
/// JSON settings under <c>%LOCALAPPDATA%\ValheimServerManager</c> (override with <c>VSM_DATA_DIR</c>).
/// Writes are atomic and the previous file is kept as <c>settings.json.bak</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ValheimServerManager.v1");
    private readonly Lock _gate = new();

    public JsonSettingsStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory
            ?? System.Environment.GetEnvironmentVariable("VSM_DATA_DIR")
            ?? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ValheimServerManager");
    }

    public string DataDirectory { get; }

    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    public AppSettings Load()
    {
        lock (_gate)
        {
            foreach (var candidate in new[] { SettingsPath, SettingsPath + ".bak" })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    var settings = JsonSerializer.Deserialize(File.ReadAllText(candidate), SettingsJsonContext.Default.AppSettings);
                    if (settings is null)
                    {
                        continue;
                    }

                    Migrate(settings);
                    foreach (var profile in settings.Profiles)
                    {
                        profile.Password = settings.ProtectedPasswords.TryGetValue(profile.Id, out var protectedValue)
                            ? Unprotect(protectedValue)
                            : string.Empty;
                    }

                    return settings;
                }
                catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
                {
                    // Try the backup copy.
                }
            }

            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            Directory.CreateDirectory(DataDirectory);
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            settings.ProtectedPasswords = settings.Profiles
                .Where(p => !string.IsNullOrEmpty(p.Password))
                .ToDictionary(p => p.Id, p => Protect(p.Password));

            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings), new UTF8Encoding(false));
            if (File.Exists(SettingsPath))
            {
                File.Replace(temp, SettingsPath, SettingsPath + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, SettingsPath);
            }
        }
    }

    private static void Migrate(AppSettings settings)
    {
        // Schema 1 is the first public format; future migrations go here, in order.
        settings.Profiles ??= [];
        settings.ProtectedPasswords ??= [];
    }

    private static string Protect(string value) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string value) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
