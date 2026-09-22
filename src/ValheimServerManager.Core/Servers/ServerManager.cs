using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Localization;
using ValheimServerManager.Core.Platform;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Settings;

namespace ValheimServerManager.Core.Servers;

/// <summary>
/// Owns the settings and one <see cref="ServerController"/> per profile. Also notices servers that
/// were started outside the manager (e.g. from a .bat) and attaches to the matching profile.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerManager : IAsyncDisposable
{
    private readonly ISettingsStore _store;
    private readonly BackupService _backups;
    private readonly IServerProcessLauncher _launcher;
    private readonly ISignalSender _signals;
    private readonly IServerProcessLocator _locator;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly Dictionary<Guid, ServerController> _controllers = [];
    private readonly Lock _gate = new();
    private IReadOnlyList<RunningServer> _unmanaged = [];

    public ServerManager(
        ISettingsStore store,
        BackupService backups,
        IServerProcessLauncher launcher,
        ISignalSender signals,
        IServerProcessLocator locator,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        _store = store;
        _backups = backups;
        _launcher = launcher;
        _signals = signals;
        _locator = locator;
        _time = time;
        _loggers = loggers;
        _logger = loggers.CreateLogger<ServerManager>();
        Settings = store.Load();
        foreach (var profile in Settings.Profiles)
        {
            CreateController(profile);
        }
    }

    public event EventHandler? ProfilesChanged;

    public event EventHandler<IReadOnlyList<RunningServer>>? UnmanagedServersChanged;

    /// <summary>Raised for every controller, so the UI can subscribe once.</summary>
    public event EventHandler<ServerAlert>? AlertRaised;

    public AppSettings Settings { get; }

    public BackupService Backups => _backups;

    public string DataDirectory => _store.DataDirectory;

    public IReadOnlyList<ServerProfile> Profiles
    {
        get
        {
            lock (_gate)
            {
                return Settings.Profiles.Select(p => p.Clone()).ToArray();
            }
        }
    }

    public IReadOnlyList<RunningServer> UnmanagedServers
    {
        get
        {
            lock (_gate)
            {
                return _unmanaged;
            }
        }
    }

    public bool AnyActive
    {
        get
        {
            lock (_gate)
            {
                return _controllers.Values.Any(c => c.Status.IsActive);
            }
        }
    }

    public ServerController GetController(Guid profileId)
    {
        lock (_gate)
        {
            return _controllers.TryGetValue(profileId, out var controller)
                ? controller
                : throw new KeyNotFoundException(string.Format(CultureInfo.CurrentCulture, Strings.Manager_ProfileDoesNotExist, profileId));
        }
    }

    public IReadOnlyList<ServerController> Controllers
    {
        get
        {
            lock (_gate)
            {
                return _controllers.Values.ToArray();
            }
        }
    }

    public ServerProfile AddProfile(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            Settings.Profiles.Add(profile.Clone());
            Settings.SelectedProfileId = profile.Id;
            CreateController(profile);
            Persist();
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public void SaveProfile(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            var index = Settings.Profiles.FindIndex(p => p.Id == profile.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException(Strings.Manager_ProfileNotFound);
            }

            var passwordChanged = Settings.Profiles[index].Password != profile.Password;
            Settings.Profiles[index] = profile.Clone();
            _controllers[profile.Id].UpdateProfile(profile);
            Persist();
            _logger.LogInformation("Profile {Name} saved{Password}", profile.DisplayName, passwordChanged ? " (password changed)" : string.Empty);
        }

        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveProfileAsync(Guid profileId)
    {
        ServerController controller;
        lock (_gate)
        {
            controller = _controllers[profileId];
            if (controller.Status.IsActive)
            {
                throw new InvalidOperationException(Strings.Manager_StopBeforeDeletingProfile);
            }

            _controllers.Remove(profileId);
            Settings.Profiles.RemoveAll(p => p.Id == profileId);
            if (Settings.SelectedProfileId == profileId)
            {
                Settings.SelectedProfileId = Settings.Profiles.FirstOrDefault()?.Id;
            }

            Persist();
        }

        controller.AlertRaised -= ForwardAlert;
        await controller.DisposeAsync().ConfigureAwait(false);
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SaveSettings()
    {
        lock (_gate)
        {
            Persist();
        }
    }

    /// <summary>Scans running valheim_server processes and attaches the ones that match a profile.</summary>
    public async Task RefreshRunningServersAsync(CancellationToken ct = default)
    {
        IReadOnlyList<RunningServer> running;
        try
        {
            running = await Task.Run(_locator.FindRunningServers, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Failed to list server processes");
            return;
        }

        var unmanaged = new List<RunningServer>();
        foreach (var server in running)
        {
            ServerController? owner;
            lock (_gate)
            {
                owner = _controllers.Values.FirstOrDefault(c => c.Status.ProcessId == server.ProcessId)
                        ?? _controllers.Values.FirstOrDefault(c => server.Matches(c.Profile));
            }

            if (owner is null)
            {
                unmanaged.Add(server);
            }
            else if (owner.Status.ProcessId != server.ProcessId && !owner.Status.IsActive)
            {
                await owner.AttachAsync(server).ConfigureAwait(false);
            }
        }

        bool changed;
        lock (_gate)
        {
            changed = !_unmanaged.Select(s => s.ProcessId).SequenceEqual(unmanaged.Select(s => s.ProcessId));
            _unmanaged = unmanaged;
        }

        if (changed)
        {
            UnmanagedServersChanged?.Invoke(this, unmanaged);
        }
    }

    /// <summary>Creates a profile from a running server that no profile knows about yet.</summary>
    public ServerProfile AdoptRunningServer(RunningServer server, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        var profile = BatchFileImporter.FromCommandLine(server.CommandLine, out _);
        profile.DisplayName = server.ServerName ?? Strings.Manager_ImportedServerName;
        profile.ServerDirectory = Path.GetDirectoryName(server.ExecutablePath) ?? string.Empty;
        if (server.SaveDirectoryIsDefault)
        {
            profile.SaveDirectory = server.SaveDirectory;
        }

        if (password is not null)
        {
            profile.Password = password;
        }

        return AddProfile(profile);
    }

    public static IReadOnlyList<string> DetectServerInstallations()
    {
        try
        {
            return ValheimPaths.FindDedicatedServerInstallations();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private void CreateController(ServerProfile profile)
    {
        var controller = new ServerController(
            profile, _backups, _launcher, _signals, _locator, _time,
            _loggers.CreateLogger($"Server.{profile.DisplayName}"));
        controller.AlertRaised += ForwardAlert;
        _controllers[profile.Id] = controller;
    }

    private void ForwardAlert(object? sender, ServerAlert alert) => AlertRaised?.Invoke(sender, alert);

    private void Persist() => _store.Save(Settings);

    public async ValueTask DisposeAsync()
    {
        ServerController[] controllers;
        lock (_gate)
        {
            controllers = _controllers.Values.ToArray();
            _controllers.Clear();
        }

        foreach (var controller in controllers)
        {
            controller.AlertRaised -= ForwardAlert;
            await controller.DisposeAsync().ConfigureAwait(false);
        }
    }
}
