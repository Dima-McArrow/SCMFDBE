using Csmfd.Core.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Csmfd.Server.Win32;

/// <summary>
/// Releases input and blocks new gestures while the Windows session is locked or detached from the
/// console (spec 41.11 item 7). Event driven (WM_WTSSESSION_CHANGE through SystemEvents), no polling.
/// Lock and detach are tracked separately: on a fast user switch back, ConsoleConnect arrives while
/// the lock screen is still up, and input must stay blocked until SessionUnlock.
/// </summary>
internal sealed partial class SessionMonitor : IDisposable
{
    private readonly InputExecutor _executor;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private bool _locked;
    private bool _detached;
    private bool _disposed;

    /// <summary>Must be created on the tray UI thread so SystemEvents uses its message loop.</summary>
    public SessionMonitor(InputExecutor executor, ILogger logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // SystemEvents holds static handlers: unsubscribe or this instance lives forever.
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool blocked;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    _locked = true;
                    break;
                case SessionSwitchReason.SessionUnlock:
                    _locked = false;
                    break;
                case SessionSwitchReason.ConsoleDisconnect:
                case SessionSwitchReason.RemoteDisconnect:
                case SessionSwitchReason.SessionLogoff:
                    _detached = true;
                    break;
                case SessionSwitchReason.ConsoleConnect:
                case SessionSwitchReason.RemoteConnect:
                case SessionSwitchReason.SessionLogon:
                    _detached = false;
                    break;
                default:
                    return; // SessionRemoteControl and future values do not change input safety.
            }

            blocked = _locked || _detached;
        }

        // The executor has its own lock; calling it outside ours keeps the lock order simple.
        _executor.SetSessionLocked(blocked);
        LogSessionChanged(_logger, e.Reason, blocked);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {Reason}; input blocked: {Blocked}")]
    private static partial void LogSessionChanged(ILogger logger, SessionSwitchReason reason, bool blocked);
}
