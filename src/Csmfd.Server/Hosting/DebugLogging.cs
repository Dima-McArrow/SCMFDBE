using Microsoft.Extensions.Logging;

namespace Csmfd.Server.Hosting;

/// <summary>
/// Temporary debug logging (spec 41.16): switched on locally from the tray and back off by itself after
/// <see cref="Duration"/>, so a forgotten switch never keeps writing detail during play. Always off at start.
/// Debug messages follow the same rules as the rest of the log: no tokens, codes, player names, chat text,
/// raw Game.log lines or full local paths.
/// </summary>
internal sealed partial class DebugLogging : IDisposable
{
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(15);

    private readonly FileLoggerProvider _provider;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private DateTimeOffset? _until;

    public DebugLogging(FileLoggerProvider provider, TimeProvider time, ILogger logger)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timer = time.CreateTimer(static s => ((DebugLogging)s!).Expire(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised on any thread (the expiry comes from a timer).</summary>
    public event Action? Changed;

    /// <summary>When debug logging switches itself off; null while it is off.</summary>
    public DateTimeOffset? Until
    {
        get
        {
            lock (_gate)
            {
                return _until;
            }
        }
    }

    /// <summary>Debug for <see cref="Duration"/> from now; calling it again restarts the window.</summary>
    public void Enable()
    {
        DateTimeOffset until;
        lock (_gate)
        {
            until = _time.GetUtcNow() + Duration;
            _until = until;
            _provider.MinimumLevel = LogLevel.Debug;
            _timer.Change(Duration, Timeout.InfiniteTimeSpan);
        }

        LogEnabled(_logger, until);
        Changed?.Invoke();
    }

    public void Disable() => TurnOff(expired: false);

    public void Dispose() => _timer.Dispose();

    private void Expire() => TurnOff(expired: true);

    private void TurnOff(bool expired)
    {
        lock (_gate)
        {
            // A late callback from an earlier window must not cut a window that was restarted meanwhile.
            if (_until is not { } until || (expired && _time.GetUtcNow() < until))
            {
                return;
            }

            _until = null;
            _provider.MinimumLevel = LogLevel.Information;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        LogDisabled(_logger, expired);
        Changed?.Invoke();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Debug logging on until {Until:u}")]
    private static partial void LogEnabled(ILogger logger, DateTimeOffset until);

    [LoggerMessage(Level = LogLevel.Information, Message = "Debug logging off (expired: {Expired})")]
    private static partial void LogDisabled(ILogger logger, bool expired);
}
