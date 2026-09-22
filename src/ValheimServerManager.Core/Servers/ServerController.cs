using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Localization;
using ValheimServerManager.Core.Logs;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Servers;

/// <summary>
/// Owns the lifecycle of one server profile: pre-start checks, automatic backups, hidden launch,
/// log following, graceful stop and the emergency brake.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerController : IAsyncDisposable
{
    private const int LogCapacity = 3000;
    private const int ActivityCapacity = 300;

    private readonly BackupService _backups;
    private readonly IServerProcessLauncher _launcher;
    private readonly ISignalSender _signals;
    private readonly IServerProcessLocator _locator;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly Lock _gate = new();
    private readonly LinkedList<ServerLogLine> _log = new();
    private readonly LinkedList<ServerActivity> _activity = new();
    private readonly List<string> _loggedModifiers = [];
    private string? _knownCommandLine;

    private ServerProfile _profile;
    private ServerProfile? _launchedProfile;
    private ServerStatus _status = new();
    private Process? _process;
    private LogTailer? _tailer;
    private TaskCompletionSource<ExitInfo>? _exitSignal;
    private int? _pendingSaveNumber;
    private readonly List<string> _pendingPlatformIds = [];
    private readonly Dictionary<long, OnlinePlayer> _players = [];
    private bool _sawSaveAfterStop;
    private bool _sawShutdown;
    private bool _emergencyKill;
    private bool _forcedKill;
    private bool _replaying;
    private int _exitHandled;
    private CancellationTokenSource? _watchdog;

    public ServerController(
        ServerProfile profile,
        BackupService backups,
        IServerProcessLauncher launcher,
        ISignalSender signals,
        IServerProcessLocator locator,
        TimeProvider time,
        ILogger logger)
    {
        _profile = profile.Clone();
        _backups = backups;
        _launcher = launcher;
        _signals = signals;
        _locator = locator;
        _time = time;
        _logger = logger;
    }

    public event EventHandler<ServerStatus>? StatusChanged;

    public event EventHandler<IReadOnlyList<ServerLogLine>>? LogReceived;

    public event EventHandler<ServerAlert>? AlertRaised;

    public event EventHandler<ServerActivity>? ActivityAdded;

    public Guid ProfileId => _profile.Id;

    public ServerProfile Profile
    {
        get
        {
            lock (_gate)
            {
                return _profile.Clone();
            }
        }
    }

    public ServerStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>True when the running server does not use exactly the saved configuration.</summary>
    public bool HasPendingChanges
    {
        get
        {
            lock (_gate)
            {
                return _status.IsActive && _status.ConfigDifferences is { Count: > 0 };
            }
        }
    }

    public IReadOnlyList<ServerLogLine> RecentLog
    {
        get
        {
            lock (_gate)
            {
                return _log.ToArray();
            }
        }
    }

    public IReadOnlyList<ServerActivity> RecentActivity
    {
        get
        {
            lock (_gate)
            {
                return _activity.Reverse().ToArray();
            }
        }
    }

    public void UpdateProfile(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            if (profile.Id != _profile.Id)
            {
                throw new ArgumentException("Profile differs from the controlled one.", nameof(profile));
            }

            _profile = profile.Clone();
        }

        PublishStatus();
        if (Status.State == ServerRunState.Running)
        {
            _ = VerifyConfigurationAsync();
        }
    }

    /// <summary>
    /// Compares the saved profile with what the running server really received and applied.
    /// </summary>
    public async Task VerifyConfigurationAsync()
    {
        int? pid;
        string? commandLine;
        string[] logged;
        lock (_gate)
        {
            pid = _status.ProcessId;
            commandLine = _knownCommandLine;
            logged = [.. _loggedModifiers];
        }

        if (pid is null)
        {
            return;
        }

        if (commandLine is null)
        {
            try
            {
                commandLine = await Task.Run(() => _locator.GetCommandLine(pid.Value)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not read the server command line");
                return;
            }

            if (commandLine is null)
            {
                return;
            }

            lock (_gate)
            {
                _knownCommandLine = commandLine;
            }
        }

        var profile = Profile;
        var differences = LaunchVerification.Compare(profile, commandLine).ToList();
        var checkedCount = LaunchVerification.CheckedSettingsCount(profile);
        if (Status.HasLog && logged.Length > 0)
        {
            // The server must have applied exactly the modifiers it was given.
            var launched = BatchFileImporter.FromCommandLine(commandLine, out _);
            differences.AddRange(LaunchVerification.CompareLoggedModifiers(launched, logged));
        }

        var previous = Status.ConfigDifferences;
        SetStatus(s => s with { ConfigDifferences = differences, ConfigCheckedCount = checkedCount, ConfigCheckedAt = _time.GetLocalNow() });
        if (differences.Count == 0 && previous is not { Count: 0 })
        {
            AddActivity(ActivityKind.Lifecycle, Format(Strings.Controller_ConfigVerified, checkedCount));
        }
        else if (differences.Count > 0 && (previous is null || previous.Count != differences.Count))
        {
            AddActivity(ActivityKind.Warning,
                Strings.Controller_ConfigMismatch + " " + string.Join("; ", differences.Select(d => $"{d.Setting} ({d.Actual} → {d.Expected})")));
        }
    }

    // ------------------------------------------------------------------ checks

    public Task<IReadOnlyList<StartCheckItem>> PreflightAsync(CancellationToken ct = default) =>
        Task.Run(() => Preflight(Profile), ct);

    private IReadOnlyList<StartCheckItem> Preflight(ServerProfile profile)
    {
        var checks = new List<StartCheckItem>();

        foreach (var issue in ProfileValidator.Validate(profile))
        {
            checks.Add(new(issue.Severity == ValidationSeverity.Error ? CheckLevel.Blocker : CheckLevel.Confirm,
                "CONFIG_" + issue.Field.ToUpperInvariant(), issue.Message));
        }

        if (Status.IsActive)
        {
            checks.Add(new(CheckLevel.Blocker, "ALREADY_RUNNING", Strings.Controller_AlreadyRunning));
            return checks;
        }

        try
        {
            foreach (var running in _locator.FindRunningServers())
            {
                if (running.Matches(profile))
                {
                    checks.Add(new(CheckLevel.Blocker, "WORLD_IN_USE",
                        Format(Strings.Controller_WorldInUse, running.ProcessId)));
                }
                else if (running.Port == profile.Port)
                {
                    checks.Add(new(CheckLevel.Blocker, "PORT_IN_USE",
                        Format(Strings.Controller_PortInUse, profile.Port, running.ServerName, running.ProcessId)));
                }
            }
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or UnauthorizedAccessException or InvalidOperationException)
        {
            checks.Add(new(CheckLevel.Info, "SCAN_FAILED", Format(Strings.Controller_ScanFailed, ex.Message)));
        }

        if (checks.All(c => c.Code is not ("PORT_IN_USE" or "WORLD_IN_USE")) && IsUdpPortBusy(profile.Port))
        {
            checks.Add(new(CheckLevel.Blocker, "PORT_BUSY",
                Format(Strings.Controller_PortBusy, profile.Port, profile.Port + 1)));
        }

        if (checks.Any(c => c.Level == CheckLevel.Blocker && c.Code.StartsWith("CONFIG_SAVEDIRECTORY", StringComparison.Ordinal)))
        {
            return checks;
        }

        if (!string.IsNullOrWhiteSpace(profile.SaveDirectory) && !string.IsNullOrWhiteSpace(profile.WorldName))
        {
            var report = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName);
            foreach (var issue in report.Issues)
            {
                var level = issue.Severity switch
                {
                    IssueSeverity.Error => CheckLevel.Blocker,
                    IssueSeverity.Warning => CheckLevel.Confirm,
                    _ => issue.Code is "NEW_WORLD" ? CheckLevel.Confirm : CheckLevel.Info,
                };
                checks.Add(new(level, "WORLD_" + issue.Code, issue.Message));
            }

            if (report.IsHealthy)
            {
                AddDuplicateCheck(profile, checks);
            }
        }

        return checks;
    }

    private void AddDuplicateCheck(ServerProfile profile, List<StartCheckItem> checks)
    {
        try
        {
            var scan = WorldRepair.Scan(profile.SaveDirectory, profile.WorldName);
            if (scan.NeedsRepair)
            {
                checks.Add(new(CheckLevel.Confirm, "WORLD_DUPLICATES",
                    Format(Strings.Controller_WorldDuplicates, scan.ExtraCopies, scan.ZonesToMark, scan.Summary)));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not look for duplicate objects");
        }
    }

    private static bool IsUdpPortBusy(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                .Any(e => e.Port == port || e.Port == port + 1);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ start

    public async Task<OperationResult> StartAsync(StartOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profile = Profile;
            var checks = await Task.Run(() => Preflight(profile), ct).ConfigureAwait(false);

            if (checks.Any(c => c.Level == CheckLevel.Blocker))
            {
                return OperationResult.Fail(Strings.Controller_CannotStart, checks);
            }

            var unconfirmed = checks.Where(c => c.Level == CheckLevel.Confirm &&
                (c.Code == "WORLD_NEW_WORLD" ? !options.AllowNewWorld : !options.AcceptWarnings)).ToArray();
            if (unconfirmed.Length > 0)
            {
                return OperationResult.Fail(Strings.Controller_ConfirmWarnings, checks);
            }

            SetStatus(s => new ServerStatus
            {
                State = ServerRunState.Starting,
                CreativeActive = profile.IsCreativeEffective,
                LastExit = s.LastExit,
                LastBackupAt = s.LastBackupAt,
                LastBackupName = s.LastBackupName,
            });
            AddActivity(ActivityKind.Lifecycle, profile.IsCreativeEffective ? Strings.Controller_StartingCreative : Strings.Controller_Starting);

            var report = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName);
            var alreadyBackedUp = report.LatestSave is { } latest && _backups.List(profile, includeGameAutoBackups: false)
                .Any(b => b.HasManifest && b.SaveNumber == latest.Number &&
                          b.WorldName.Equals(profile.WorldName, StringComparison.OrdinalIgnoreCase) &&
                          b.Kind != BackupKind.Quarantine);
            if (profile.BackupBeforeStart && report.IsHealthy && alreadyBackedUp)
            {
                AddActivity(ActivityKind.Backup, Format(Strings.Controller_SaveAlreadyBackedUp, report.LatestSave!.Number));
            }
            else if (profile.BackupBeforeStart && report.IsHealthy)
            {
                AddActivity(ActivityKind.Backup, Strings.Controller_BackingUpBeforeStart);
                try
                {
                    var backup = await _backups.CreateAsync(profile, BackupKind.PreStart, ct: ct).ConfigureAwait(false);
                    RecordBackup(backup);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "Backup before starting failed");
                    SetStatus(s => s with { State = ServerRunState.Stopped });
                    Raise(AlertLevel.Error, Strings.Controller_BackupFailedTitle, Format(Strings.Controller_NotStartedBackupFailed, ex.Message));
                    return OperationResult.Fail(Format(Strings.Controller_BackupBeforeStartFailed, ex.Message));
                }
            }

            Directory.CreateDirectory(profile.SaveDirectory);
            try
            {
                LogArchiver.Archive(profile.LogFilePath, profile.ArchivedLogsDirectory);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not archive the previous log");
            }

            Process process;
            try
            {
                process = _launcher.Launch(profile);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                SetStatus(s => s with { State = ServerRunState.Stopped });
                Raise(AlertLevel.Error, Strings.Controller_StartFailedTitle, ex.Message);
                return OperationResult.Fail(Format(Strings.Controller_WindowsCouldNotStart, ex.Message));
            }

            lock (_gate)
            {
                _launchedProfile = profile.Clone();
            }

            await BeginTrackingAsync(process, profile.LogFilePath, attached: false, creative: profile.IsCreativeEffective).ConfigureAwait(false);
            _logger.LogInformation("Server {Name} started (PID {Pid})", profile.ServerName, process.Id);
            AddActivity(ActivityKind.Lifecycle, Format(Strings.Controller_ProcessStarted, process.Id));
            return new OperationResult(true, Strings.Controller_ServerStarting, checks);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Starts following a server that was already running when the manager opened.</summary>
    public async Task AttachAsync(RunningServer running)
    {
        ArgumentNullException.ThrowIfNull(running);
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Status.IsActive)
            {
                return;
            }

            Process process;
            try
            {
                process = Process.GetProcessById(running.ProcessId);
            }
            catch (ArgumentException)
            {
                return;
            }

            SetStatus(s => new ServerStatus
            {
                State = ServerRunState.Running,
                IsAttached = true,
                HasLog = running.LogFile is not null && File.Exists(running.LogFile),
                CreativeActive = running.CreativeMode,
                StartedAt = running.StartedAt is { } started ? new DateTimeOffset(started) : null,
                LastExit = s.LastExit,
                LastBackupAt = s.LastBackupAt,
                LastBackupName = s.LastBackupName,
            });
            lock (_gate)
            {
                _launchedProfile = null;
            }

            AddActivity(ActivityKind.Lifecycle, Format(Strings.Controller_AlreadyRunningAttached, running.ProcessId));
            lock (_gate)
            {
                _knownCommandLine = running.CommandLine;
            }

            if (running.LogFile is null)
            {
                Raise(AlertLevel.Warning, Strings.Controller_NoLogTitle, Strings.Controller_NoLogMessage);
            }

            await BeginTrackingAsync(process, running.LogFile, attached: true, creative: running.CreativeMode).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }

        await VerifyConfigurationAsync().ConfigureAwait(false);
    }

    private async Task BeginTrackingAsync(Process process, string? logFile, bool attached, bool creative)
    {
        await StopTailerAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _process = process;
            _exitSignal = new TaskCompletionSource<ExitInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingSaveNumber = null;
            _loggedModifiers.Clear();
            _pendingPlatformIds.Clear();
            _players.Clear();
            if (!attached)
            {
                _knownCommandLine = null;
            }
            _sawSaveAfterStop = false;
            _sawShutdown = false;
            _emergencyKill = false;
            _forcedKill = false;
            _exitHandled = 0;
        }

        StartWatchdog(process.Id);
        SetStatus(s => s with
        {
            ProcessId = process.Id,
            StartedAt = s.StartedAt ?? _time.GetLocalNow(),
            CreativeActive = creative,
            Emergency = null,
            StopTimedOut = false,
            StopRequestedAt = null,
            ModeVerified = null,
            ConfigDifferences = null,
            ConfigCheckedCount = 0,
            ConfigCheckedAt = null,
        });

        try
        {
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
            if (process.HasExited)
            {
                OnProcessExited(process, EventArgs.Empty);
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _logger.LogWarning(ex, "No permission to follow process {Pid}", process.Id);
        }

        if (logFile is null)
        {
            return;
        }

        if (attached)
        {
            // Rebuild state from what is already in the log without acting on old events.
            _replaying = true;
            try
            {
                var replay = new LogTailer(logFile, _ => { });
                HandleLines(replay.Poll());
                _tailer = new LogTailer(logFile, HandleLines, fromStart: false);
            }
            finally
            {
                _replaying = false;
            }
        }
        else
        {
            _tailer = new LogTailer(logFile, HandleLines);
        }

        _tailer.Start();
    }

    // ------------------------------------------------------------------ log

    private void HandleLines(IReadOnlyList<string> lines)
    {
        var batch = new List<ServerLogLine>(lines.Count);
        foreach (var line in lines)
        {
            var evt = ServerLogParser.Parse(line);
            batch.Add(new ServerLogLine(_time.GetLocalNow(), line, evt is not null));
            if (evt is not null)
            {
                try
                {
                    Handle(evt);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Failed to handle event {Kind}", evt.Kind);
                }
            }
        }

        lock (_gate)
        {
            foreach (var line in batch)
            {
                _log.AddLast(line);
                if (_log.Count > LogCapacity)
                {
                    _log.RemoveFirst();
                }
            }
        }

        if (!_replaying)
        {
            LogReceived?.Invoke(this, batch);
        }
    }

    private void Handle(ServerLogEvent evt)
    {
        switch (evt.Kind)
        {
            case ServerLogEventKind.WorldLoadRequested:
                SetStatus(s => s with { LoadedSaveNumber = evt.Number });
                break;

            case ServerLogEventKind.MissingWorldData when evt.Number > 0:
                if (_replaying)
                {
                    Raise(AlertLevel.Critical, Strings.Controller_RegeneratedTitle,
                        Format(Strings.Controller_RegeneratedMessage, Path.GetFileName(evt.Text)));
                }
                else
                {
                    EmergencyKill(evt);
                }

                break;

            case ServerLogEventKind.WorldLoading:
                SetStatus(s => s with { LoadedZdos = evt.Count });
                if (!_replaying)
                {
                    AddActivity(ActivityKind.Lifecycle, Format(Strings.Controller_LoadingObjects, evt.Count, evt.Number));
                }

                break;

            case ServerLogEventKind.ModifierApplied:
                lock (_gate)
                {
                    _loggedModifiers.Add(evt.Text ?? string.Empty);
                }

                break;

            case ServerLogEventKind.ServerOnline:
                SetStatus(s => s.State == ServerRunState.Starting ? s with { State = ServerRunState.Running } : s);
                if (!_replaying)
                {
                    _ = VerifyConfigurationAsync();
                    AddActivity(ActivityKind.Lifecycle, Strings.Controller_ServerOnline);
                    Raise(AlertLevel.Success, Strings.Controller_ServerOnlineTitle, Format(Strings.Controller_AcceptingConnections, Profile.ServerName));
                }

                break;

            case ServerLogEventKind.JoinCode:
                SetStatus(s => s with { JoinCode = evt.Text });
                break;

            case ServerLogEventKind.PlayerCount:
                var previous = Status.PlayerCount;
                SetStatus(s => s with { PlayerCount = evt.Number ?? 0, PublicAddress = evt.Text ?? s.PublicAddress });
                if (evt.Number == 0)
                {
                    lock (_gate)
                    {
                        _players.Clear();
                        _pendingPlatformIds.Clear();
                    }

                    PublishPlayers();
                }

                if (!_replaying && evt.Number is { } count && count > previous)
                {
                    AddActivity(ActivityKind.Player, Format(Strings.Controller_SomeoneConnecting, count));
                }

                break;

            case ServerLogEventKind.PlayerConnected:
                lock (_gate)
                {
                    if (evt.Text is { } pid && !_pendingPlatformIds.Contains(pid))
                    {
                        _pendingPlatformIds.Add(pid);
                    }
                }

                break;

            case ServerLogEventKind.PlayerSpawned:
                OnPlayerSpawned(evt);
                break;

            case ServerLogEventKind.PlayerDied when !_replaying:
                AddActivity(ActivityKind.Player, Format(Strings.Controller_PlayerDied, evt.Text));
                break;

            case ServerLogEventKind.PlayerLeft:
                OnPlayerLeft(p => p.OwnerId == evt.Count);
                break;

            case ServerLogEventKind.PlayerDisconnected:
                lock (_gate)
                {
                    _pendingPlatformIds.Remove(evt.Text ?? string.Empty);
                }

                OnPlayerLeft(p => p.PlatformId == evt.Text);
                break;

            case ServerLogEventKind.SaveStarted:
                lock (_gate)
                {
                    _pendingSaveNumber = evt.Number;
                }

                break;

            case ServerLogEventKind.SaveCompleted:
                OnSaveCompleted(evt);
                break;

            case ServerLogEventKind.ShutdownComplete:
                lock (_gate)
                {
                    _sawShutdown = true;
                }

                break;

            case ServerLogEventKind.Error when !_replaying:
                AddActivity(ActivityKind.Warning, evt.Text ?? evt.Line);
                break;
        }
    }

    private void OnPlayerSpawned(ServerLogEvent evt)
    {
        var owner = evt.Count ?? 0;
        var name = evt.Text ?? "?";
        bool isNew;
        lock (_gate)
        {
            isNew = !_players.TryGetValue(owner, out var existing);
            if (isNew)
            {
                // Clients connect first and spawn a few seconds later, in the same order.
                string? platformId = null;
                if (_pendingPlatformIds.Count > 0)
                {
                    platformId = _pendingPlatformIds[0];
                    _pendingPlatformIds.RemoveAt(0);
                }

                _players[owner] = new OnlinePlayer(name, owner, platformId, When(evt));
            }
            else if (existing!.Name != name)
            {
                _players[owner] = existing with { Name = name };
            }
        }

        PublishPlayers();
        if (isNew && !_replaying)
        {
            AddActivity(ActivityKind.Player, Format(Strings.Controller_PlayerJoinedWorld, name));
            Raise(AlertLevel.Info, Strings.Controller_PlayerJoinedTitle, Format(Strings.Controller_PlayerJoinedServer, name, Profile.ServerName));
        }
    }

    private void OnPlayerLeft(Func<OnlinePlayer, bool> match)
    {
        List<OnlinePlayer> gone;
        lock (_gate)
        {
            gone = _players.Values.Where(match).ToList();
            foreach (var player in gone)
            {
                _players.Remove(player.OwnerId);
            }
        }

        if (gone.Count == 0)
        {
            return;
        }

        PublishPlayers();
        if (!_replaying)
        {
            foreach (var player in gone)
            {
                AddActivity(ActivityKind.Player, Format(Strings.Controller_PlayerLeft, player.Name));
                Raise(AlertLevel.Info, Strings.Controller_PlayerLeftTitle, Format(Strings.Controller_PlayerLeftServer, player.Name, Profile.ServerName));
            }
        }
    }

    private void PublishPlayers()
    {
        OnlinePlayer[] list;
        lock (_gate)
        {
            list = _players.Values.OrderBy(p => p.Since).ToArray();
        }

        SetStatus(s => s with { OnlinePlayers = list });
    }

    private DateTimeOffset When(ServerLogEvent evt) =>
        evt.Timestamp is { } ts ? new DateTimeOffset(ts) : _time.GetLocalNow();

    private void OnSaveCompleted(ServerLogEvent evt)
    {
        int? number;
        lock (_gate)
        {
            number = _pendingSaveNumber;
            if (_status.State == ServerRunState.Stopping)
            {
                _sawSaveAfterStop = true;
            }
        }

        SetStatus(s => s with
        {
            LastSaveNumber = number ?? s.LastSaveNumber,
            LastSaveAt = evt.Timestamp is { } ts ? new DateTimeOffset(ts) : _time.GetLocalNow(),
            LastSaveMilliseconds = evt.Count,
        });

        if (_replaying)
        {
            return;
        }

        AddActivity(ActivityKind.Save, number is null ? Strings.Controller_WorldSaved : Format(Strings.Controller_WorldSavedDetails, number, evt.Count));

        // Confirm that creative mode on/off actually reached the world file.
        var profile = Profile;
        var report = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName);
        if (report.Metadata is { } meta)
        {
            var expected = Status.CreativeActive;
            var ok = meta.IsCreative == expected;
            SetStatus(s => s with { ModeVerified = ok });
            if (!ok)
            {
                Raise(AlertLevel.Error, Strings.Controller_ModeMismatchTitle,
                    expected
                        ? Strings.Controller_ModeMismatchCreative
                        : Strings.Controller_ModeMismatchNormal);
            }
        }
    }

    private void EmergencyKill(ServerLogEvent evt)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _emergencyKill = true;
        }

        var message = Format(Strings.Controller_EmergencyMessage, Path.GetFileName(evt.Text));
        _logger.LogCritical("Emergency: {Line}", evt.Line);
        SetStatus(s => s with { Emergency = message });
        AddActivity(ActivityKind.Error, Strings.Controller_EmergencyActivity);
        Raise(AlertLevel.Critical, Strings.Controller_EmergencyTitle, message);

        try
        {
            process?.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogError(ex, "Failed to kill the server in the emergency");
        }
    }

    // ------------------------------------------------------------------ stop

    public async Task<OperationResult> StopAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        Process? process;
        TaskCompletionSource<ExitInfo>? exit;
        try
        {
            lock (_gate)
            {
                process = _process;
                exit = _exitSignal;
            }

            if (process is null || exit is null || !Status.IsActive)
            {
                return OperationResult.Fail(Strings.Controller_NotRunning);
            }

            SetStatus(s => s with { State = ServerRunState.Stopping, StopRequestedAt = _time.GetLocalNow(), StopTimedOut = false });
            AddActivity(ActivityKind.Lifecycle, Strings.Controller_StoppingSafely);

            var result = await _signals.SendCtrlCAsync(process.Id, ct).ConfigureAwait(false);
            if (result != SignalResult.Sent && result != SignalResult.ProcessNotFound)
            {
                SetStatus(s => s with { State = ServerRunState.Running, StopRequestedAt = null });
                Raise(AlertLevel.Error, Strings.Controller_CouldNotStopTitle, Format(Strings.Controller_StopSignalFailed, result));
                return OperationResult.Fail(Format(Strings.Controller_StopSignalFailedStillRunning, result));
            }
        }
        finally
        {
            _operation.Release();
        }

        return await WaitForStopAsync(TimeSpan.FromSeconds(Profile.StopTimeoutSeconds), ct).ConfigureAwait(false);
    }

    /// <summary>Waits for a stop already in progress (after a timeout the user may choose to keep waiting).</summary>
    public async Task<OperationResult> WaitForStopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        TaskCompletionSource<ExitInfo>? exit;
        lock (_gate)
        {
            exit = _exitSignal;
        }

        if (exit is null)
        {
            return OperationResult.Ok(Strings.Controller_AlreadyStopped);
        }

        try
        {
            var info = await exit.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
            return info.Clean
                ? OperationResult.Ok(Strings.Controller_StoppedWorldSaved)
                : OperationResult.Fail(info.Reason);
        }
        catch (TimeoutException)
        {
            SetStatus(s => s with { StopTimedOut = true });
            Raise(AlertLevel.Warning, Strings.Controller_SlowStopTitle, Format(Strings.Controller_SlowStopMessage, timeout.TotalSeconds));
            return OperationResult.Fail(Strings.Controller_NotStoppedYet);
        }
    }

    /// <summary>Kills the process without saving. Only on explicit user request.</summary>
    public async Task ForceKillAsync()
    {
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? process;
            lock (_gate)
            {
                process = _process;
                _forcedKill = true;
            }

            if (process is null)
            {
                return;
            }

            AddActivity(ActivityKind.Warning, Strings.Controller_ForcedByUser);
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    public async Task<OperationResult> RestartAsync(StartOptions options, CancellationToken ct = default)
    {
        if (Status.IsActive)
        {
            var stop = await StopAsync(ct).ConfigureAwait(false);
            if (!stop.Success)
            {
                return stop;
            }
        }

        return await StartAsync(options, ct).ConfigureAwait(false);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _ = Task.Run(HandleExitAsync);
    }

    /// <summary>Backup for processes whose exit event we cannot subscribe to (other user rights, races).</summary>
    private void StartWatchdog(int processId)
    {
        _watchdog?.Cancel();
        _watchdog?.Dispose();
        var cts = new CancellationTokenSource();
        _watchdog = cts;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), _time);
            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false))
                {
                    bool alive;
                    try
                    {
                        using var p = Process.GetProcessById(processId);
                        alive = !p.HasExited;
                    }
                    catch (ArgumentException)
                    {
                        alive = false;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        alive = true;
                    }

                    if (!alive)
                    {
                        await HandleExitAsync().ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task HandleExitAsync()
    {
        Process? process;
        TaskCompletionSource<ExitInfo>? exit;
        bool stopRequested, sawSave, sawShutdown, emergency, forced;
        lock (_gate)
        {
            process = _process;
            exit = _exitSignal;
            if (process is null || exit is null || exit.Task.IsCompleted || _exitHandled == 1)
            {
                return;
            }

            _exitHandled = 1;
            _watchdog?.Cancel();

            stopRequested = _status.State == ServerRunState.Stopping;
            emergency = _emergencyKill;
            forced = _forcedKill;
        }

        // Let the tailer read the final lines (save confirmation, shutdown).
        await Task.Delay(TimeSpan.FromMilliseconds(900), _time).ConfigureAwait(false);
        if (_tailer is not null)
        {
            try
            {
                HandleLines(_tailer.Poll());
            }
            catch (IOException)
            {
            }
        }

        await StopTailerAsync().ConfigureAwait(false);

        lock (_gate)
        {
            sawSave = _sawSaveAfterStop;
            sawShutdown = _sawShutdown;
        }

        int? code = null;
        try
        {
            code = process.ExitCode;
        }
        catch (InvalidOperationException)
        {
        }

        var hasLog = Status.HasLog;
        ExitInfo info;
        if (emergency)
        {
            info = new ExitInfo(_time.GetLocalNow(), code, false, Strings.Controller_ExitEmergency);
        }
        else if (forced)
        {
            info = new ExitInfo(_time.GetLocalNow(), code, false, Strings.Controller_ExitForced);
        }
        else if (stopRequested && (sawSave || !hasLog) && (sawShutdown || code == 0 || !hasLog))
        {
            info = new ExitInfo(_time.GetLocalNow(), code, true, Strings.Controller_ExitClean);
        }
        else if (stopRequested)
        {
            info = new ExitInfo(_time.GetLocalNow(), code, false, Strings.Controller_ExitNoFinalSave);
        }
        else
        {
            info = new ExitInfo(_time.GetLocalNow(), code, false, Strings.Controller_ExitUnexpected);
        }

        var state = info.Clean || stopRequested || emergency || forced ? ServerRunState.Stopped : ServerRunState.Crashed;
        SetStatus(s => s with
        {
            State = state,
            ProcessId = null,
            PlayerCount = 0,
            OnlinePlayers = [],
            JoinCode = null,
            StopTimedOut = false,
            LastExit = info,
            IsAttached = false,
            ConfigDifferences = null,
        });

        lock (_gate)
        {
            _process = null;
            _launchedProfile = null;
            _knownCommandLine = null;
        }

        process.Dispose();
        AddActivity(info.Clean ? ActivityKind.Lifecycle : ActivityKind.Error, info.Reason);
        if (!info.Clean && !emergency)
        {
            Raise(stopRequested ? AlertLevel.Warning : AlertLevel.Error, Strings.Controller_ServerStoppedTitle, info.Reason);
        }

        await AfterStopAsync(emergency).ConfigureAwait(false);
        exit.TrySetResult(info);
        if (info.Clean)
        {
            Raise(AlertLevel.Success, Strings.Controller_ServerShutDownTitle, Strings.Controller_WorldSavedBeforeShutdown);
        }
    }

    private async Task AfterStopAsync(bool emergency)
    {
        var profile = Profile;
        var report = WorldInspector.Inspect(profile.SaveDirectory, profile.WorldName);
        if (report.HasErrors)
        {
            Raise(AlertLevel.Critical, Strings.Controller_WorldProblemAfterStopTitle,
                string.Join(" ", report.Issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Message)));
            return;
        }

        if (emergency || !profile.BackupAfterStop || !report.IsHealthy)
        {
            return;
        }

        try
        {
            AddActivity(ActivityKind.Backup, Strings.Controller_BackingUpAfterStop);
            var backup = await _backups.CreateAsync(profile, BackupKind.PostStop).ConfigureAwait(false);
            RecordBackup(backup);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Backup after stopping failed");
            Raise(AlertLevel.Error, Strings.Controller_BackupAfterStopFailedTitle, ex.Message);
            return;
        }

        await FixWorldAsync(profile).ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps the world from drifting back into the two problems it had: objects the game generated
    /// twice and items marked as made with cheats. Runs on the world the post-stop backup just saved.
    /// </summary>
    private async Task FixWorldAsync(ServerProfile profile)
    {
        if (!profile.FixWorldAfterStop)
        {
            return;
        }

        try
        {
            var repaired = await Task.Run(() => WorldRepair.Scan(profile.SaveDirectory, profile.WorldName) is { NeedsRepair: true }
                ? WorldRepair.Repair(profile.SaveDirectory, profile.WorldName)
                : null).ConfigureAwait(false);
            if (repaired is { } r)
            {
                var message = Format(Strings.Controller_MaintenanceRepaired, r.RemovedObjects, r.ZonesMarked);
                AddActivity(ActivityKind.Save, message);
                Raise(AlertLevel.Info, Strings.Controller_WorldFixedAfterStopTitle, message);
            }

            var cleaned = await Task.Run(() => WorldCheatMarks.Scan(profile.SaveDirectory, profile.WorldName) is { NeedsCleaning: true }
                ? WorldCheatMarks.Clean(profile.SaveDirectory, profile.WorldName)
                : null).ConfigureAwait(false);
            if (cleaned is { } c)
            {
                var message = Format(Strings.Controller_MaintenanceCleaned, c.ClearedObjects, c.ClearedItems);
                AddActivity(ActivityKind.Save, message);
                Raise(AlertLevel.Info, Strings.Controller_MarksRemovedAfterStopTitle, message);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "World maintenance after stopping failed");
            Raise(AlertLevel.Warning, Strings.Controller_MaintenanceNotDoneTitle,
                Strings.Controller_MaintenanceNotDoneMessage + " " + ex.Message);
        }
    }

    /// <summary>
    /// Removes duplicated world objects and marks populated zones as generated (see <see cref="WorldRepair"/>).
    /// Only with the server stopped and the world not used by another process; a backup is taken first.
    /// </summary>
    public async Task<OperationResult> RepairWorldAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profile = Profile;
            if (Status.IsActive)
            {
                return OperationResult.Fail(Strings.Controller_StopBeforeRepair);
            }

            var inUse = Preflight(profile).Where(c => c.Code is "WORLD_IN_USE" or "ALREADY_RUNNING").ToArray();
            if (inUse.Length > 0)
            {
                return OperationResult.Fail(Strings.Controller_WorldInUseByOther, inUse);
            }

            var scan = await Task.Run(() => WorldRepair.Scan(profile.SaveDirectory, profile.WorldName), ct).ConfigureAwait(false);
            if (!scan.NeedsRepair)
            {
                return OperationResult.Ok(Strings.Controller_NothingToRepair);
            }

            AddActivity(ActivityKind.Backup, Strings.Controller_BackingUpBeforeRepair);
            var backup = await _backups.CreateAsync(profile, BackupKind.Manual, Strings.Controller_NoteBeforeRepair, ct).ConfigureAwait(false);
            RecordBackup(backup);

            var result = await Task.Run(() => WorldRepair.Repair(profile.SaveDirectory, profile.WorldName), ct).ConfigureAwait(false);
            var message = Format(Strings.Controller_WorldRepaired, result.RemovedObjects, result.ZonesMarked, result.OldSaveNumber, result.NewSaveNumber, backup.Name);
            _logger.LogInformation("World repaired: {Removed} copies removed, {Zones} zones marked (save {Old} -> {New}), backup {Backup}",
                result.RemovedObjects, result.ZonesMarked, result.OldSaveNumber, result.NewSaveNumber, backup.Name);
            AddActivity(ActivityKind.Save, message);
            Raise(AlertLevel.Success, Strings.Controller_WorldRepairedTitle, message);
            return OperationResult.Ok(message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "World repair failed");
            return OperationResult.Fail(Strings.Controller_RepairNotDone + " " + ex.Message);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Clears the "made with cheats" mark from the world so items stack again (see <see cref="WorldCheatMarks"/>).
    /// Only with the server stopped; a backup is taken first.
    /// </summary>
    public async Task<OperationResult> CleanCheatMarksAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profile = Profile;
            if (Status.IsActive)
            {
                return OperationResult.Fail(Strings.Controller_StopBeforeCleaning);
            }

            var inUse = Preflight(profile).Where(c => c.Code is "WORLD_IN_USE" or "ALREADY_RUNNING").ToArray();
            if (inUse.Length > 0)
            {
                return OperationResult.Fail(Strings.Controller_WorldInUseByOther, inUse);
            }

            var scan = await Task.Run(() => WorldCheatMarks.Scan(profile.SaveDirectory, profile.WorldName), ct).ConfigureAwait(false);
            if (!scan.NeedsCleaning)
            {
                return OperationResult.Ok(Strings.Controller_NothingToClean);
            }

            AddActivity(ActivityKind.Backup, Strings.Controller_BackingUpBeforeCleaning);
            var backup = await _backups.CreateAsync(profile, BackupKind.Manual, Strings.Controller_NoteBeforeCleaning, ct).ConfigureAwait(false);
            RecordBackup(backup);

            var result = await Task.Run(() => WorldCheatMarks.Clean(profile.SaveDirectory, profile.WorldName), ct).ConfigureAwait(false);
            var message = Format(Strings.Controller_MarksRemoved, result.ClearedObjects, result.ClearedItems, result.OldSaveNumber, result.NewSaveNumber, backup.Name);
            _logger.LogInformation("Cheat marks cleared from {Objects} objects and {Items} items (save {Old} -> {New}), backup {Backup}",
                result.ClearedObjects, result.ClearedItems, result.OldSaveNumber, result.NewSaveNumber, backup.Name);
            AddActivity(ActivityKind.Save, message);
            Raise(AlertLevel.Success, Strings.Controller_MarksRemovedTitle, message);
            return OperationResult.Ok(message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Clearing the cheat marks failed");
            return OperationResult.Fail(Strings.Controller_CleaningNotDone + " " + ex.Message);
        }
        finally
        {
            _operation.Release();
        }
    }

    public void RecordBackup(BackupEntry backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        SetStatus(s => s with { LastBackupAt = backup.CreatedAt, LastBackupName = backup.Name });
        AddActivity(ActivityKind.Backup, Format(Strings.Controller_BackupVerified, backup.Name));
    }

    // ------------------------------------------------------------------ plumbing

    private async Task StopTailerAsync()
    {
        var tailer = Interlocked.Exchange(ref _tailer, null);
        if (tailer is not null)
        {
            await tailer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void SetStatus(Func<ServerStatus, ServerStatus> update)
    {
        ServerStatus next;
        lock (_gate)
        {
            next = update(_status);
            if (ReferenceEquals(next, _status))
            {
                return;
            }

            _status = next;
        }

        StatusChanged?.Invoke(this, next);
    }

    private void PublishStatus() => StatusChanged?.Invoke(this, Status);

    private void AddActivity(ActivityKind kind, string message)
    {
        var item = new ServerActivity(_time.GetLocalNow(), kind, message);
        lock (_gate)
        {
            _activity.AddLast(item);
            if (_activity.Count > ActivityCapacity)
            {
                _activity.RemoveFirst();
            }
        }

        ActivityAdded?.Invoke(this, item);
    }

    private void Raise(AlertLevel level, string title, string message) =>
        AlertRaised?.Invoke(this, new ServerAlert(level, title, message, _time.GetLocalNow()));

    private static string Format(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, format, args);

    public async ValueTask DisposeAsync()
    {
        if (_watchdog is not null)
        {
            await _watchdog.CancelAsync().ConfigureAwait(false);
            _watchdog.Dispose();
        }

        await StopTailerAsync().ConfigureAwait(false);
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            process.Dispose();
        }

        _operation.Dispose();
    }
}
