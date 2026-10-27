namespace Csmfd.Core.GameLog;

/// <summary>
/// Drives the tailer (spec 41.14): FileSystemWatcher callbacks only signal one reader; a backup check
/// runs every 2 s while the game runs and every 10 s otherwise, in case notifications were missed.
/// Exactly one poll runs at a time; a flood is processed in bounded slices without blocking input.
/// </summary>
public sealed class GameLogService : IDisposable
{
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);

    private readonly LogTailer _tailer;
    private readonly GameLogProcessor _processor;
    private readonly Func<bool> _isGameRunning;
    private readonly ITimer _backup;
    private FileSystemWatcher? _watcher;
    private int _pollRequested;
    private int _polling;
    private bool _disposed;

    public GameLogService(LogTailer tailer, GameLogProcessor processor, TimeProvider time, Func<bool> isGameRunning)
    {
        _tailer = tailer ?? throw new ArgumentNullException(nameof(tailer));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _isGameRunning = isGameRunning ?? throw new ArgumentNullException(nameof(isGameRunning));
        ArgumentNullException.ThrowIfNull(time);
        _backup = time.CreateTimer(static s => ((GameLogService)s!).Signal(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null && Directory.Exists(directory))
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            _watcher.Changed += (_, _) => Signal();
            _watcher.Created += (_, _) => Signal();
            _watcher.Renamed += (_, _) => Signal();
            _watcher.Deleted += (_, _) => Signal();

            // Buffer overflow: just re-check identity/length/offset; the tailer recovers by itself.
            _watcher.Error += (_, _) => Signal();
            _watcher.EnableRaisingEvents = true;
        }

        Signal();
    }

    public void Signal()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _pollRequested, 1);
        if (Interlocked.CompareExchange(ref _polling, 1, 0) == 0)
        {
            _ = Task.Run(PollLoop);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _watcher?.Dispose();
        _backup.Dispose();
    }

    private void PollLoop()
    {
        try
        {
            while (Interlocked.Exchange(ref _pollRequested, 0) == 1 && !_disposed)
            {
                _tailer.Poll(_processor.OnLine, _processor.OnGenerationReset);
                _processor.OnLogStatus(_tailer.Status, _tailer.Generation);

                if (_tailer.Status == Csmfd.Protocol.LogStatus.CatchingUp)
                {
                    // More data than one slice: continue after yielding the thread.
                    Interlocked.Exchange(ref _pollRequested, 1);
                    Thread.Yield();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
            if (Volatile.Read(ref _pollRequested) == 1 && !_disposed)
            {
                Signal();
            }

            if (!_disposed)
            {
                _backup.Change(_isGameRunning() ? ActiveInterval : IdleInterval, Timeout.InfiniteTimeSpan);
            }
        }
    }
}
