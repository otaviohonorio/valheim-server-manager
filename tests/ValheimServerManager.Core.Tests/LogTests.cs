using System.Text;
using ValheimServerManager.Core.Logs;

namespace ValheimServerManager.Core.Tests;

public class ServerLogParserTests
{
    [Theory]
    [InlineData("09/16/2026 05:21:52: ZNet.LoadWorld: MeuMundo (MeuMundo), save number 30", ServerLogEventKind.WorldLoadRequested, 30)]
    [InlineData("09/16/2026 04:11:57:   missing C:/Users/x/AppData/LocalLow/IronGate/Valheim/worlds_local/MeuMundo/_main.17.db2", ServerLogEventKind.MissingWorldData, 17)]
    [InlineData("09/16/2026 06:11:39: World save (1/5) Cloud & Backup checks done [0ms] => Save number 32", ServerLogEventKind.SaveStarted, 32)]
    [InlineData("09/16/2026 05:25:04: Player joined server \"Meu Servidor\" that has join code 123456, now 1 player(s)", ServerLogEventKind.PlayerCount, 1)]
    [InlineData("09/16/2026 05:22:01: Session \"Meu Servidor\" with join code 123456 and IP 203.0.113.10:2456 is active with 0 player(s)", ServerLogEventKind.PlayerCount, 0)]
    public void Parses_numbered_events(string line, ServerLogEventKind kind, int number)
    {
        var evt = ServerLogParser.Parse(line);
        Assert.NotNull(evt);
        Assert.Equal(kind, evt.Kind);
        Assert.Equal(number, evt.Number);
    }

    [Fact]
    public void Parses_player_lines_from_a_real_crossplay_session()
    {
        var connected = ServerLogParser.Parse("09/16/2026 20:42:00: PlayFab socket with remote ID playfab/0123456789ABCDEF received local Platform ID Steam_76561190000000001");
        Assert.Equal(ServerLogEventKind.PlayerConnected, connected!.Kind);
        Assert.Equal("Steam_76561190000000001", connected.Text);

        var spawned = ServerLogParser.Parse("09/16/2026 20:42:24: Got character ZDOID from Bjorn : -123456789:1");
        Assert.Equal(ServerLogEventKind.PlayerSpawned, spawned!.Kind);
        Assert.Equal("Bjorn", spawned.Text);
        Assert.Equal(-123456789, spawned.Count);

        var died = ServerLogParser.Parse("09/16/2026 20:50:00: Got character ZDOID from Bjorn : 0:0");
        Assert.Equal(ServerLogEventKind.PlayerDied, died!.Kind);

        var left = ServerLogParser.Parse("09/16/2026 21:49:40: Destroying abandoned non persistent zdo -123456789:6956 owner -123456789");
        Assert.Equal(ServerLogEventKind.PlayerLeft, left!.Kind);
        Assert.Equal(-123456789, left.Count);
    }

    [Fact]
    public void Parses_steam_connection_lines()
    {
        Assert.Equal("Steam_76561190000000002", ServerLogParser.Parse("09/16/2026 20:42:00: Got connection SteamID 76561190000000002")!.Text);
        var closed = ServerLogParser.Parse("09/16/2026 20:42:00: Closing socket 76561190000000002");
        Assert.Equal(ServerLogEventKind.PlayerDisconnected, closed!.Kind);
        Assert.Equal("Steam_76561190000000002", closed.Text);
    }

    [Fact]
    public void Parses_timestamp()
    {
        var evt = ServerLogParser.Parse("09/16/2026 05:21:57: Game server connected");
        Assert.Equal(ServerLogEventKind.ServerOnline, evt!.Kind);
        Assert.Equal(new DateTime(2026, 9, 16, 5, 21, 57), evt.Timestamp);
    }

    [Fact]
    public void Parses_join_code_and_address()
    {
        Assert.Equal("123456", ServerLogParser.Parse("09/16/2026 05:21:59: Session \"Meu Servidor\" registered with join code 123456")!.Text);
        var active = ServerLogParser.Parse("09/16/2026 05:22:01: Session \"X\" with join code 1 and IP 203.0.113.10:2456 is active with 2 player(s)");
        Assert.Equal("203.0.113.10:2456", active!.Text);
    }

    [Fact]
    public void Parses_world_loading_with_localized_thousands()
    {
        var evt = ServerLogParser.Parse("09/16/2026 05:21:52: ZDOMan.LoadChunks - Starting to load 38.757 zdos from 4 Chunks. SessionID: -1, WorldVersion: 41 [DeepNorth]");
        Assert.Equal(ServerLogEventKind.WorldLoading, evt!.Kind);
        Assert.Equal(38757, evt.Count);
        Assert.Equal(4, evt.Number);
    }

    [Fact]
    public void Parses_save_completion_time()
    {
        var evt = ServerLogParser.Parse("09/16/2026 06:11:39: World save (5/5) done. Total time [52ms]");
        Assert.Equal(ServerLogEventKind.SaveCompleted, evt!.Kind);
        Assert.Equal(52, evt.Count);
    }

    [Theory]
    [InlineData("09/16/2026 06:11:41: Net scene destroyed", ServerLogEventKind.ShutdownComplete)]
    [InlineData("09/16/2026 05:25:27: Got character ZDOID from Bjorn : -1495850011:2", ServerLogEventKind.PlayerSpawned)]
    [InlineData("09/16/2026 05:21:49: Setting world modifier: deathpenalty->casual", ServerLogEventKind.ModifierApplied)]
    [InlineData("09/16/2026 04:13:05: Removing orphan CHUNK file: C:/x/20_1e__1_18.chunk", ServerLogEventKind.OrphanRemoved)]
    public void Parses_simple_events(string line, ServerLogEventKind kind)
    {
        Assert.Equal(kind, ServerLogParser.Parse(line)!.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Unloading 2 unused Assets to reduce memory usage.")]
    [InlineData("The referenced script on this Behaviour (Game Object '<null>') is missing!")]
    public void Ignores_noise(string line)
    {
        Assert.Null(ServerLogParser.Parse(line));
    }
}

public class LogTailerTests
{
    [Fact]
    public void Returns_only_complete_new_lines()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("server.log");
        var tailer = new LogTailer(path, _ => { });

        Assert.Empty(tailer.Poll());

        Append(path, "linha 1\r\nlinha 2\r\nparcial");
        Assert.Equal(["linha 1", "linha 2"], tailer.Poll());

        Append(path, " completa\n");
        Assert.Equal(["parcial completa"], tailer.Poll());
        Assert.Empty(tailer.Poll());
    }

    [Fact]
    public void Starts_over_when_the_file_is_recreated()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("server.log");
        Append(path, "sessão antiga com bastante texto\n");
        var tailer = new LogTailer(path, _ => { });
        Assert.Single(tailer.Poll());

        File.WriteAllText(path, "nova\n");
        Assert.Equal(["nova"], tailer.Poll());
    }

    [Fact]
    public void Can_skip_existing_content()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("server.log");
        Append(path, "velho\n");
        var tailer = new LogTailer(path, _ => { }, fromStart: false);
        Append(path, "novo\n");
        Assert.Equal(["novo"], tailer.Poll());
    }

    [Fact]
    public void Archiver_moves_the_log_and_keeps_a_limit()
    {
        using var tmp = new TempDir();
        var archive = tmp.Combine("logs");
        for (var i = 0; i < 4; i++)
        {
            var log = tmp.Combine("server.log");
            File.WriteAllText(log, $"sessão {i}");
            File.SetLastWriteTime(log, new DateTime(2026, 9, 16, 10, i, 0));
            Assert.NotNull(LogArchiver.Archive(log, archive, keep: 3));
            Assert.False(File.Exists(log));
        }

        Assert.Equal(3, Directory.GetFiles(archive).Length);
        Assert.Null(LogArchiver.Archive(tmp.Combine("server.log"), archive));
    }

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes);
    }
}
