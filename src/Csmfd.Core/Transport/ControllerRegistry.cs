using Csmfd.Core.Diagnostics;
using Csmfd.Core.Input;
using Csmfd.Protocol;

namespace Csmfd.Core.Transport;

/// <summary>Outbound side of a connection, as seen by the registry.</summary>
public interface IControllerConnection
{
    Guid Id { get; }

    Guid DeviceId { get; }

    string DeviceName { get; }

    /// <summary>Queues an already serialized message; must not block.</summary>
    void EnqueueControl(byte[] frame);

    /// <summary>Requests a graceful close; must not block or call into the executor synchronously.</summary>
    void RequestClose(int closeCode, string description);
}

/// <summary>
/// One authenticated controller at a time (spec 41.7). Routes executor results to the owning
/// connection, enforces the unauthenticated connection limit and handles replacement/revoke.
/// </summary>
public sealed class ControllerRegistry : IExecutorOutput
{
    private readonly Lock _gate = new();
    private readonly int _maxUnauthenticated;
    private InputExecutor? _executor;
    private IControllerConnection? _controller;
    private int _unauthenticated;

    public ControllerRegistry(int maxUnauthenticated = 2)
    {
        _maxUnauthenticated = maxUnauthenticated;
    }

    /// <summary>A down/hold reached Windows (used by the ATC tracker).</summary>
    public event Action<Guid, ActionSpec>? InputStartedNotified;

    public event Action? InputBlockedNotified;

    /// <summary>Raised with the device name when a controller becomes active, null when none.</summary>
    public event Action<IControllerConnection?>? ControllerChanged;

    public IControllerConnection? Controller => Volatile.Read(ref _controller);

    /// <summary>Optional local input monitor (tray debug window).</summary>
    public InputMonitor? Monitor { get; set; }

    /// <summary>Breaks the construction cycle: the executor needs this registry as its output.</summary>
    public void Attach(InputExecutor executor) => _executor = executor ?? throw new ArgumentNullException(nameof(executor));

    public bool TryEnterUnauthenticated()
    {
        lock (_gate)
        {
            if (_unauthenticated >= _maxUnauthenticated)
            {
                return false;
            }

            _unauthenticated++;
            return true;
        }
    }

    public void LeaveUnauthenticated()
    {
        lock (_gate)
        {
            _unauthenticated = Math.Max(0, _unauthenticated - 1);
        }
    }

    /// <summary>
    /// Makes an authenticated connection the controller. The previous controller's input is
    /// released before the new one can send anything (spec 41.7), then it is closed with 4001.
    /// </summary>
    public void Activate(IControllerConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        IControllerConnection? previous;
        lock (_gate)
        {
            previous = _controller;
            _controller = null;
        }

        if (previous is not null)
        {
            _executor?.CancelConnection(previous.Id, CancelReason.ClientReplaced);
            previous.RequestClose(CloseCodes.Replaced, "Replaced by another client");
        }

        lock (_gate)
        {
            _controller = connection;
        }

        ControllerChanged?.Invoke(connection);
    }

    /// <summary>Called by a closing connection; releases its input and clears it if still current.</summary>
    public void Deactivate(IControllerConnection connection, CancelReason reason)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _executor?.CancelConnection(connection.Id, reason);
        bool cleared;
        lock (_gate)
        {
            cleared = ReferenceEquals(_controller, connection);
            if (cleared)
            {
                _controller = null;
            }
        }

        if (cleared)
        {
            ControllerChanged?.Invoke(null);
        }
    }

    /// <summary>Closes the controller if it belongs to a revoked device (spec 41.5 item 9).</summary>
    public void OnDeviceRevoked(Guid deviceId)
    {
        var current = Controller;
        if (current is not null && current.DeviceId == deviceId)
        {
            _executor?.CancelConnection(current.Id, CancelReason.Revoked);
            current.RequestClose(CloseCodes.Revoked, "Device revoked");
        }
    }

    public void Ack(Guid connectionId, Guid commandId, AckStatus status, RejectReason? reason = null)
    {
        Monitor?.Result(commandId, status, reason);
        Find(connectionId)?.EnqueueControl(ServerMessageWriter.Ack(commandId, status, reason));
    }

    public void InputError(Guid connectionId, ErrorCode code, Guid commandId, CancelReason reason)
    {
        Monitor?.Cancelled(commandId, code, reason);
        Find(connectionId)?.EnqueueControl(ServerMessageWriter.Error(code, MessageFor(code), commandId, reason));
    }

    public void InputStarted(Guid connectionId, Guid commandId, ActionSpec spec) =>
        InputStartedNotified?.Invoke(commandId, spec);

    public void InputBlocked() => InputBlockedNotified?.Invoke();

    private static string MessageFor(ErrorCode code) => code switch
    {
        ErrorCode.AutoReleased => "Input auto-released",
        _ => "Input cancelled",
    };

    /// <summary>Messages for a connection that is gone (or was replaced) are dropped.</summary>
    private IControllerConnection? Find(Guid connectionId)
    {
        var current = Controller;
        return current is not null && current.Id == connectionId ? current : null;
    }
}
