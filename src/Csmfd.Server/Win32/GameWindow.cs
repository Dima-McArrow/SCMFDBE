using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Csmfd.Core.Input;
using Csmfd.Protocol;
using Microsoft.Extensions.Logging;
using static Csmfd.Server.Win32.NativeMethods;

namespace Csmfd.Server.Win32;

/// <summary>
/// "Is the configured StarCitizen.exe in the foreground?" (spec 41.11 item 1). The full image path is
/// compared, not just the process name. If Windows refuses even a limited query (the game runs under
/// an anti-cheat), the process name is used as a logged fallback instead of blocking all input.
/// </summary>
internal sealed partial class Win32GameWindow : IGameWindow, IDisposable
{
    private const long ScanIntervalMs = 30_000;

    private readonly string _exePath;
    private readonly string _processName;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private Process? _game;
    private long _lastScan;
    private int _fallbackLogged;

    public Win32GameWindow(string exePath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        _exePath = Path.GetFullPath(exePath);
        _processName = Path.GetFileNameWithoutExtension(_exePath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public GameFocus Check()
    {
        var foreground = GetForegroundWindow();
        if (foreground != 0 && GetWindowThreadProcessId(foreground, out var processId) != 0 && IsGameProcess(processId))
        {
            Remember(processId);
            return GameFocus.Focused;
        }

        return IsRunning() ? GameFocus.Background : GameFocus.NotRunning;
    }

    /// <summary>
    /// Cheap presence check: a known game process is only asked HasExited. Enumerating every process
    /// (expensive) happens at most every 30 s while the game is not known; a game start is normally
    /// detected earlier through the foreground event when its window gets focus.
    /// </summary>
    public bool IsRunning()
    {
        lock (_gate)
        {
            if (_game is not null)
            {
                if (IsAlive(_game))
                {
                    return true;
                }

                _game.Dispose();
                _game = null;
            }

            if (Environment.TickCount64 - _lastScan < ScanIntervalMs && _lastScan != 0)
            {
                return false;
            }

            _lastScan = Environment.TickCount64;
        }

        var processes = Process.GetProcessesByName(_processName);
        try
        {
            foreach (var process in processes)
            {
                if (IsGameProcess((uint)process.Id))
                {
                    Remember((uint)process.Id);
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _game?.Dispose();
            _game = null;
        }
    }

    private void Remember(uint processId)
    {
        lock (_gate)
        {
            if (_game is not null && _game.Id == processId && IsAlive(_game))
            {
                return;
            }

            _game?.Dispose();
            try
            {
                _game = Process.GetProcessById((int)processId);
            }
            catch (ArgumentException)
            {
                _game = null; // Exited meanwhile.
            }
        }
    }

    /// <summary>HasExited may be denied for a protected game process: fall back to a PID + name check.</summary>
    private bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            try
            {
                using var current = Process.GetProcessById(process.Id);
                return string.Equals(current.ProcessName, _processName, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    private bool IsGameProcess(uint processId)
    {
        var imagePath = TryGetImagePath(processId, out var accessDenied);
        if (imagePath is not null)
        {
            return string.Equals(Path.GetFullPath(imagePath), _exePath, StringComparison.OrdinalIgnoreCase);
        }

        if (!accessDenied)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0)
        {
            LogNameFallback(_logger, _processName);
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.ProcessName, _processName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // Process exited meanwhile.
        }
    }

    private static unsafe string? TryGetImagePath(uint processId, out bool accessDenied)
    {
        accessDenied = false;
        var handle = OpenProcess(ProcessQueryLimitedInformation, inheritHandle: false, processId);
        if (handle == 0)
        {
            const int errorAccessDenied = 5;
            accessDenied = Marshal.GetLastPInvokeError() == errorAccessDenied;
            return null;
        }

        try
        {
            const int capacity = 1024;
            var buffer = stackalloc char[capacity];
            var size = (uint)capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot read the image path of {ProcessName}; falling back to process name matching")]
    private static partial void LogNameFallback(ILogger logger, string processName);
}

/// <summary>
/// Publishes game.running/focused and triggers safety release (spec 41.11 item 7): foreground changes
/// arrive through a WinEvent hook (no polling); process presence is re-checked every 2 s while the game
/// runs and every 10 s otherwise.
/// </summary>
internal sealed class GameMonitor : IDisposable
{
    private static readonly TimeSpan RunningInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);

    private readonly Win32GameWindow _window;
    private readonly Csmfd.Core.State.StateStore _store;
    private readonly InputExecutor _executor;
    private readonly string? _build;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private readonly WinEventProc _callback;

    private nint _hook;
    private bool _wasRunning;
    private bool _wasFocused;

    public GameMonitor(Win32GameWindow window, Csmfd.Core.State.StateStore store, InputExecutor executor, TimeProvider time, string? build)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        ArgumentNullException.ThrowIfNull(time);
        _build = build;
        _callback = OnWinEvent;
        _timer = time.CreateTimer(static s => ((GameMonitor)s!).Refresh(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Raised when a running game process disappears.</summary>
    public event Action? GameExited;

    /// <summary>Must be called on a thread with a message loop (the tray UI thread).</summary>
    public void Start()
    {
        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            0,
            Marshal.GetFunctionPointerForDelegate(_callback),
            0,
            0,
            WinEventOutOfContext);
        Refresh();
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            UnhookWinEvent(_hook);
            _hook = 0;
        }

        _timer.Dispose();
    }

    /// <summary>Reads the numeric build (RequestedP4ChangeNum) from build_manifest.id, null if unknown.</summary>
    public static string? ReadBuild(string gameExePath)
    {
        try
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(gameExePath));
            var manifest = root is null ? null : Path.Combine(root, "build_manifest.id");
            if (manifest is null || !File.Exists(manifest))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("Data", out var data)
                   && data.TryGetProperty("RequestedP4ChangeNum", out var change)
                   && change.ValueKind == JsonValueKind.String
                   && TextRules.IsValidText(change.GetString()!, 1, ProtocolLimits.MaxNameScalars)
                ? change.GetString()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint eventThread, uint eventTime) =>
        Refresh();

    private void Refresh()
    {
        bool exited;
        lock (_gate)
        {
            var focus = _window.Check();
            var running = focus != GameFocus.NotRunning;
            var focused = focus == GameFocus.Focused;

            if (_wasFocused && !focused)
            {
                _executor.CancelAll(Csmfd.Protocol.CancelReason.FocusLost);
            }

            exited = _wasRunning && !running;
            if (exited)
            {
                _executor.CancelAll(Csmfd.Protocol.CancelReason.GameEnded);
            }

            _wasRunning = running;
            _wasFocused = focused;
            _store.Update(s => s with { Game = new GameState(running, focused, running ? _build : null) });
            _timer.Change(running ? RunningInterval : IdleInterval, Timeout.InfiniteTimeSpan);
        }

        if (exited)
        {
            GameExited?.Invoke();
        }
    }
}
