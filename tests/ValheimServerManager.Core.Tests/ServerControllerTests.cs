using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using ValheimServerManager.Core.Backups;
using ValheimServerManager.Core.Processes;
using ValheimServerManager.Core.Profiles;
using ValheimServerManager.Core.Servers;
using ValheimServerManager.Core.Worlds;

namespace ValheimServerManager.Core.Tests;

/// <summary>
/// Drives the controller with a harmless stand-in process (ping) and a fake log, so the whole
/// lifecycle — including the emergency brake — runs without a real Valheim server.
/// </summary>
public sealed class ServerControllerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly ServerProfile _profile;
    private readonly FakeLauncher _launcher = new();
    private readonly FakeSignals _signals;
    private readonly ServerController _controller;
    private readonly BackupService _backups = new(TimeProvider.System);
    private readonly NoServersLocator _locator = new();

    public ServerControllerTests()
    {
        _profile = _tmp.Profile();
        _profile.StopTimeoutSeconds = 15;
        _signals = new FakeSignals(_profile);
        _controller = new ServerController(
            _profile, _backups, _launcher, _signals, _locator, TimeProvider.System, NullLogger.Instance);
        _locator.RunningAs = _profile;
    }

    private string WorldDir => WorldFolder.WorldDirectory(_profile.SaveDirectory, _profile.WorldName);

    [Fact]
    public async Task Refuses_to_start_a_world_whose_latest_save_is_incomplete()
    {
        TestWorlds.WriteSave(WorldDir, "MeuMundo", 17, TestWorlds.DefaultChunks, writeDb2: false);

        var result = await _controller.StartAsync(StartOptions.Confirmed, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(result.Checks, c => c is { Level: CheckLevel.Blocker, Code: "WORLD_INCOMPLETE_LATEST_SAVE" });
        Assert.Equal(0, _launcher.Launches);
        Assert.Equal(ServerRunState.Stopped, _controller.Status.State);
    }

    [Fact]
    public async Task New_world_needs_explicit_confirmation()
    {
        var result = await _controller.StartAsync(StartOptions.Default, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.NeedsConfirmation);
        Assert.Contains(result.Checks, c => c.Code == "WORLD_NEW_WORLD");
        Assert.Equal(0, _launcher.Launches);
    }

    [Fact]
    public async Task Full_lifecycle_backs_up_starts_goes_online_and_stops_cleanly()
    {
        TestWorlds.WriteSave(WorldDir, "MeuMundo", 7, TestWorlds.DefaultChunks);

        var start = await _controller.StartAsync(StartOptions.Default, TestContext.Current.CancellationToken);
        Assert.True(start.Success, start.Message + string.Join(" | ", start.Checks.Select(c => c.Message)));
        Assert.Equal(ServerRunState.Starting, _controller.Status.State);
        Assert.Contains(_backups.List(_profile), b => b.Kind == BackupKind.PreStart && b.SaveNumber == 7);

        WriteLog(
            "09/16/2026 05:21:52: ZNet.LoadWorld: MeuMundo (MeuMundo), save number 7",
            "09/16/2026 05:21:52: ZDOMan.LoadChunks - Starting to load 38.720 zdos from 3 Chunks.",
            "09/16/2026 05:21:57: Game server connected",
            "09/16/2026 05:21:59: Session \"Servidor de Teste\" registered with join code 123456",
            "09/16/2026 05:25:04: Player joined server \"Servidor de Teste\" that has join code 123456, now 1 player(s)");
        await WaitUntil(() => _controller.Status is { State: ServerRunState.Running, PlayerCount: 1, JoinCode: "123456" });
        Assert.Equal(38720, _controller.Status.LoadedZdos);

        var stop = await _controller.StopAsync(TestContext.Current.CancellationToken);

        Assert.True(stop.Success, stop.Message);
        var status = _controller.Status;
        Assert.Equal(ServerRunState.Stopped, status.State);
        Assert.True(status.LastExit!.Clean);
        Assert.Equal(8, status.LastSaveNumber);
        Assert.True(status.ModeVerified);
        Assert.Contains(_backups.List(_profile), b => b.Kind == BackupKind.PostStop && b.SaveNumber == 8);
    }

    [Fact]
    public async Task Verifies_that_the_running_server_uses_every_saved_option()
    {
        _profile.Resources = ResourceRate.More;
        _profile.PassiveMobs = true;
        _controller.UpdateProfile(_profile);
        _locator.RunningAs = _profile;
        TestWorlds.WriteSave(WorldDir, "MeuMundo", 7, TestWorlds.DefaultChunks);
        Assert.True((await _controller.StartAsync(StartOptions.Default, TestContext.Current.CancellationToken)).Success);

        WriteLog(
            "09/16/2026 05:21:49: Setting world modifier preset: normal",
            "09/16/2026 05:21:49: Setting world modifier: resources->more",
            "09/16/2026 05:21:57: Game server connected");
        await WaitUntil(() => _controller.Status.ConfigDifferences is not null);

        Assert.Empty(_controller.Status.ConfigDifferences!);
        Assert.False(_controller.HasPendingChanges);
        Assert.True(_controller.Status.ConfigCheckedCount >= 15);

        // The user changes the password while the server runs: the difference shows up at once.
        var edited = _controller.Profile;
        edited.Password = "senhaNova99";
        _controller.UpdateProfile(edited);
        await WaitUntil(() => _controller.Status.ConfigDifferences is { Count: > 0 });

        var diff = Assert.Single(_controller.Status.ConfigDifferences!);
        Assert.Equal("Senha", diff.Setting);
        Assert.DoesNotContain("senhaNova99", diff.Expected + diff.Actual);
        Assert.True(_controller.HasPendingChanges);

        await _controller.ForceKillAsync();
        await WaitUntil(() => !_controller.Status.IsActive);
    }

    [Fact]
    public async Task Reports_a_modifier_the_server_did_not_apply()
    {
        _profile.Resources = ResourceRate.More;
        _controller.UpdateProfile(_profile);
        _locator.RunningAs = _profile;
        TestWorlds.WriteSave(WorldDir, "MeuMundo", 7, TestWorlds.DefaultChunks);
        Assert.True((await _controller.StartAsync(StartOptions.Default, TestContext.Current.CancellationToken)).Success);

        WriteLog(
            "09/16/2026 05:21:49: Setting world modifier preset: normal",
            "09/16/2026 05:21:57: Game server connected");
        await WaitUntil(() => _controller.Status.ConfigDifferences is not null);

        Assert.Contains(_controller.Status.ConfigDifferences!, d => d.Setting.Contains("resources") && d.Actual == "(não aplicado)");

        await _controller.ForceKillAsync();
        await WaitUntil(() => !_controller.Status.IsActive);
    }

    [Fact]
    public async Task Missing_world_data_triggers_the_emergency_brake()
    {
        TestWorlds.WriteSave(WorldDir, "MeuMundo", 7, TestWorlds.DefaultChunks);
        var alerts = new List<ServerAlert>();
        _controller.AlertRaised += (_, a) => alerts.Add(a);

        Assert.True((await _controller.StartAsync(StartOptions.Default, TestContext.Current.CancellationToken)).Success);
        WriteLog(
            "09/16/2026 04:11:57: ZNet.LoadWorld: MeuMundo (MeuMundo), save number 7",
            "09/16/2026 04:11:57:   missing H:/x/worlds_local/MeuMundo/_main.7.db2");

        await WaitUntil(() => _controller.Status.State == ServerRunState.Stopped);

        var status = _controller.Status;
        Assert.NotNull(status.Emergency);
        Assert.False(status.LastExit!.Clean);
        Assert.Contains(alerts, a => a.Level == AlertLevel.Critical);
        Assert.True(_launcher.LastProcess!.HasExited);
        Assert.DoesNotContain(_backups.List(_profile), b => b.Kind == BackupKind.PostStop);
    }

    [Fact]
    public async Task Missing_db2_of_a_brand_new_world_is_normal()
    {
        Assert.True((await _controller.StartAsync(StartOptions.Confirmed, TestContext.Current.CancellationToken)).Success);
        WriteLog(
            "09/16/2026 04:11:57: ZNet.LoadWorld: MeuMundo (MeuMundo), save number 0",
            "09/16/2026 04:11:57:   missing H:/x/worlds_local/MeuMundo/_main.0.db2",
            "09/16/2026 04:12:10: Game server connected");

        await WaitUntil(() => _controller.Status.State == ServerRunState.Running);
        Assert.Null(_controller.Status.Emergency);

        await _controller.ForceKillAsync();
        await WaitUntil(() => _controller.Status.State == ServerRunState.Stopped);
    }

    [Fact]
    public async Task Process_that_dies_on_its_own_is_reported_as_crashed()
    {
        TestWorlds.WriteSave(WorldDir, "MeuMundo", 7, TestWorlds.DefaultChunks);
        Assert.True((await _controller.StartAsync(StartOptions.Default, TestContext.Current.CancellationToken)).Success);
        WriteLog("09/16/2026 05:21:57: Game server connected");
        await WaitUntil(() => _controller.Status.State == ServerRunState.Running);

        _launcher.LastProcess!.Kill();

        await WaitUntil(() => _controller.Status.State == ServerRunState.Crashed);
        Assert.False(_controller.Status.LastExit!.Clean);
    }

    [Fact]
    public async Task Mode_mismatch_after_save_is_reported()
    {
        TestWorlds.WriteSave(WorldDir, "MeuMundo", 7, TestWorlds.DefaultChunks, keys: ["nobuildcost"]);
        var alerts = new List<ServerAlert>();
        _controller.AlertRaised += (_, a) => alerts.Add(a);
        Assert.True((await _controller.StartAsync(StartOptions.Default, TestContext.Current.CancellationToken)).Success);

        // The profile is in normal mode, but the world the server saves still carries nobuildcost.
        WriteLog(
            "09/16/2026 05:21:57: Game server connected",
            "09/16/2026 05:40:00: World save (1/5) Cloud & Backup checks done [0ms] => Save number 7",
            "09/16/2026 05:40:00: World save (5/5) done. Total time [40ms]");

        await WaitUntil(() => _controller.Status.ModeVerified == false);
        Assert.Contains(alerts, a => a.Title.Contains("Modo"));

        await _controller.ForceKillAsync();
        await WaitUntil(() => !_controller.Status.IsActive);
    }

    private void WriteLog(params string[] lines)
    {
        Directory.CreateDirectory(_profile.SaveDirectory);
        using var stream = new FileStream(_profile.LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
        stream.Write(bytes);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20))
            {
                Assert.Fail("Condição não atingida em 20 s.");
            }

            await Task.Delay(100);
        }
    }

    public void Dispose()
    {
        try
        {
            if (_launcher.LastProcess is { HasExited: false } p)
            {
                p.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }

        _controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tmp.Dispose();
    }

    private sealed class FakeLauncher : IServerProcessLauncher
    {
        public int Launches { get; private set; }

        public Process? LastProcess { get; private set; }

        public Process Launch(ServerProfile profile)
        {
            Launches++;
            var info = new ProcessStartInfo("ping", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false };
            LastProcess = Process.Start(info)!;
            return Process.GetProcessById(LastProcess.Id);
        }
    }

    /// <summary>Simulates Valheim reacting to Ctrl+C: writes a new save, logs it and exits.</summary>
    private sealed class FakeSignals(ServerProfile profile) : ISignalSender
    {
        public Task<SignalResult> SendCtrlCAsync(int processId, CancellationToken cancellationToken = default)
        {
            var world = WorldFolder.WorldDirectory(profile.SaveDirectory, profile.WorldName);
            TestWorlds.WriteSave(world, profile.WorldName, 8, TestWorlds.DefaultChunks);
            using (var stream = new FileStream(profile.LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                stream.Write(Encoding.UTF8.GetBytes(
                    "09/16/2026 06:11:39: World save (1/5) Cloud & Backup checks done [0ms] => Save number 8\n" +
                    "09/16/2026 06:11:39: World save (5/5) done. Total time [52ms]\n" +
                    "09/16/2026 06:11:41: Net scene destroyed\n"));
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(300, cancellationToken);
                using var p = Process.GetProcessById(processId);
                p.Kill();
            }, cancellationToken);
            return Task.FromResult(SignalResult.Sent);
        }
    }

    private sealed class NoServersLocator : IServerProcessLocator
    {
        /// <summary>Profile whose arguments the fake "running process" reports.</summary>
        public ServerProfile? RunningAs { get; set; }

        public IReadOnlyList<RunningServer> FindRunningServers() => [];

        public string? GetCommandLine(int processId) =>
            RunningAs is null ? null : "\"D:\\Steam Library\\valheim_server.exe\" " + CommandLine.Join(LaunchArguments.Build(RunningAs));

        public bool IsGameRunning() => false;
    }
}
