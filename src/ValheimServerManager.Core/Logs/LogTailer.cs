using System.Globalization;
using System.Text;

namespace ValheimServerManager.Core.Logs;

/// <summary>
/// Follows a log file that another process keeps open and appends to. Polling is used on purpose:
/// change notifications are unreliable for files held open by the writer.
/// </summary>
public sealed class LogTailer : IAsyncDisposable
{
    private readonly string _path;
    private readonly TimeSpan _interval;
    private readonly Action<IReadOnlyList<string>> _onLines;
    private readonly CancellationTokenSource _cts = new();
    private readonly StringBuilder _partial = new();
    private Task? _loop;
    private long _position;

    public LogTailer(string path, Action<IReadOnlyList<string>> onLines, TimeSpan? interval = null, bool fromStart = true)
    {
        _path = path;
        _onLines = onLines;
        _interval = interval ?? TimeSpan.FromMilliseconds(500);
        _position = fromStart || !File.Exists(path) ? 0 : new FileInfo(path).Length;
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Reads whatever is new right now (also used by tests).</summary>
    public IReadOnlyList<string> Poll()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _position)
        {
            // The server recreated the log (new session).
            _position = 0;
            _partial.Clear();
        }

        if (stream.Length == _position)
        {
            return [];
        }

        stream.Seek(_position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var text = reader.ReadToEnd();
        _position = stream.Length;

        _partial.Append(text);
        var buffered = _partial.ToString();
        var lastBreak = buffered.LastIndexOf('\n');
        if (lastBreak < 0)
        {
            return [];
        }

        var complete = buffered[..lastBreak];
        _partial.Clear().Append(buffered[(lastBreak + 1)..]);
        return complete.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                var lines = Poll();
                if (lines.Count > 0)
                {
                    _onLines(lines);
                }
            }
            catch (IOException)
            {
                // File briefly locked or being replaced; try again next tick.
            }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}

/// <summary>Keeps old <c>server.log</c> files: the server overwrites it on every start.</summary>
public static class LogArchiver
{
    public static string? Archive(string logFile, string archiveDirectory, int keep = 30)
    {
        if (!File.Exists(logFile) || new FileInfo(logFile).Length == 0)
        {
            return null;
        }

        Directory.CreateDirectory(archiveDirectory);
        var stamp = File.GetLastWriteTime(logFile).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(archiveDirectory, $"server-{stamp}.log");
        var suffix = 1;
        while (File.Exists(target))
        {
            target = Path.Combine(archiveDirectory, $"server-{stamp}-{suffix++}.log");
        }

        File.Move(logFile, target);

        foreach (var old in Directory.GetFiles(archiveDirectory, "server-*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Skip(keep))
        {
            File.Delete(old);
        }

        return target;
    }
}
