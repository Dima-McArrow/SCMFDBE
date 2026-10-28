using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Unicode;
using Csmfd.Core.Diagnostics;
using Csmfd.Core.GameLog;
using Csmfd.Core.Input;
using Csmfd.Core.Security;
using Csmfd.Core.State;
using Csmfd.Protocol;
using Microsoft.Extensions.Logging;

namespace Csmfd.Core.Transport;

public sealed record ServerIdentity(Guid ServerId, string ServerName, string ServerVersion, ChatKey ChatKey);

/// <summary>Initial project limits from spec 41.6 (not Microsoft requirements, not measurements).</summary>
public sealed record SessionLimits
{
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan MessageAssemblyTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan CloseHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan StateDebounce { get; init; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public int MaxControlQueue { get; init; } = 64;

    public int MaxMessagesPerSecond { get; init; } = 100;

    public int MaxProtocolErrors { get; init; } = 20;
}

/// <summary>
/// One WebSocket connection after upgrade (spec 41.6, 41.7). One receive loop parses and
/// dispatches; one send loop serializes every outbound frame (control frames first, then a
/// single coalesced state). Nothing here ever waits for input timing or log parsing.
/// </summary>
public sealed partial class ConnectionSession : IControllerConnection, IDisposable
{
    private const int StateAwaitHello = 0;
    private const int StateAwaitPair = 1;
    private const int StateAuthenticated = 2;

    private readonly ActivityJournal? _activity;
    private Guid? _activityStream;
    private long _activitySeq;
    private int _activitySignaled;
    private bool _activityCanSend;
    private readonly WebSocket _socket;
    private readonly TimeProvider _time;
    private readonly PairingService _pairing;
    private readonly ControllerRegistry _registry;
    private readonly InputExecutor _executor;
    private readonly StateStore _store;
    private readonly ServerIdentity _identity;
    private readonly SessionLimits _limits;
    private readonly ILogger _logger;
    private readonly InputMonitor? _monitor;
    private readonly CommandGate _gate;
    private readonly ConcurrentQueue<byte[]> _control = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly long _startedAt;
    private readonly ITimer _debounceTimer;

    private CancellationTokenSource? _lifetime;
    private CloseRequest? _closeRequest;
    private int _phase = StateAwaitHello;
    private int _controlCount;
    private int _stateReady;
    private int _debouncePending;
    private int _protocolErrors;
    private bool _needsFull;
    private bool _leftUnauthenticated;
    private string _deviceName = string.Empty;
    private long _pairRequiredAt;
    private long _lastPingAt;
    private long _messageStartedAt;
    private long _rateWindowStart;
    private int _rateWindowCount;

    public ConnectionSession(
        WebSocket socket,
        TimeProvider time,
        PairingService pairing,
        ControllerRegistry registry,
        InputExecutor executor,
        StateStore store,
        ServerIdentity identity,
        ILogger logger,
        SessionLimits? limits = null,
        InputMonitor? monitor = null,
        ActivityJournal? activity = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _limits = limits ?? new SessionLimits();
        _monitor = monitor;
        _activity = activity;
        _gate = new CommandGate(time);
        _startedAt = time.GetTimestamp();
        _rateWindowStart = _startedAt;
        _debounceTimer = time.CreateTimer(static s => ((ConnectionSession)s!).OnDebounceElapsed(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Guid DeviceId { get; private set; }

    public string DeviceName => _deviceName;

    private bool IsAuthenticated => Volatile.Read(ref _phase) == StateAuthenticated;

    /// <summary>Runs until the connection ends. The caller must have entered the unauthenticated slot.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _lifetime = lifetime;
        var watchdog = _time.CreateTimer(static s => ((ConnectionSession)s!).CheckDeadlines(), this, _limits.WatchdogInterval, _limits.WatchdogInterval);
        var sendLoop = Task.Run(() => SendLoopAsync(lifetime.Token), CancellationToken.None);

        try
        {
            await ReceiveLoopAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Lifetime ended (server shutdown, close handshake timeout or slow client).
        }
        catch (WebSocketException ex)
        {
            LogSocketError(_logger, Id, ex.WebSocketErrorCode);
        }
        finally
        {
            await watchdog.DisposeAsync().ConfigureAwait(false);
            RequestClose((int)WebSocketCloseStatus.NormalClosure, "Closing");
            await WaitForSendLoopAsync(sendLoop, lifetime).ConfigureAwait(false);
            await DrainUntilPeerClosesAsync().ConfigureAwait(false);
            Shutdown();
        }
    }

    /// <summary>Call after RunAsync completed. Late enqueue/close calls are no-ops by then.</summary>
    public void Dispose()
    {
        _debounceTimer.Dispose();
        _signal.Dispose();
    }

    public void EnqueueControl(byte[] frame)
    {
        if (Volatile.Read(ref _closeRequest) is not null)
        {
            return;
        }

        if (Interlocked.Increment(ref _controlCount) > _limits.MaxControlQueue)
        {
            // Ack/error must never be silently dropped: stop input and close instead (spec 41.15).
            RequestClose(CloseCodes.Overload, "Outbound queue overflow");
            return;
        }

        _control.Enqueue(frame);
        _signal.Release();
    }

    public void RequestClose(int closeCode, string description)
    {
        if (Interlocked.CompareExchange(ref _closeRequest, new CloseRequest(closeCode, description), null) is null)
        {
            _signal.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[ProtocolLimits.MaxMessageBytes + 1];
        while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _closeRequest) is null)
        {
            var count = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                switch (result.MessageType)
                {
                    case WebSocketMessageType.Close:
                        RequestClose((int)WebSocketCloseStatus.NormalClosure, "Client closed");
                        return;
                    case WebSocketMessageType.Binary:
                        RequestClose((int)WebSocketCloseStatus.InvalidMessageType, "Binary frames are not supported");
                        return;
                }

                if (count == 0)
                {
                    Volatile.Write(ref _messageStartedAt, _time.GetTimestamp());
                }

                count += result.Count;
                if (count > ProtocolLimits.MaxMessageBytes)
                {
                    RequestClose((int)WebSocketCloseStatus.MessageTooBig, "Message too large");
                    return;
                }
            }
            while (!result.EndOfMessage);

            Volatile.Write(ref _messageStartedAt, 0);
            var receivedAt = _time.GetTimestamp();
            var payload = buffer.AsMemory(0, count);

            if (!Utf8.IsValid(payload.Span))
            {
                RequestClose((int)WebSocketCloseStatus.InvalidPayloadData, "Invalid UTF-8");
                return;
            }

            if (!CountIncomingMessage(receivedAt))
            {
                RequestClose(CloseCodes.Overload, "Too many messages");
                return;
            }

            Handle(ClientMessageParser.Parse(payload), receivedAt);
        }
    }

    private void Handle(ParseResult result, long receivedAt)
    {
        if (!result.IsSuccess)
        {
            HandleFailure(result.Failure);
            return;
        }

        switch (Volatile.Read(ref _phase))
        {
            case StateAwaitHello:
                HandleBeforeHello(result.Message);
                break;
            case StateAwaitPair:
                HandlePairing(result.Message);
                break;
            default:
                HandleAuthenticated(result.Message, receivedAt);
                break;
        }
    }

    private void HandleFailure(ParseFailure failure)
    {
        switch (failure.Kind)
        {
            case ParseFailureKind.UnsupportedVersion:
                SendError(ErrorCode.UnsupportedVersion, "Unsupported protocol version");
                RequestClose(CloseCodes.UnsupportedVersion, "Unsupported protocol version");
                return;

            case ParseFailureKind.CommandRejected when IsAuthenticated:
                _monitor?.Result(failure.CommandId!.Value, AckStatus.Rejected, failure.Reason);
                EnqueueControl(ServerMessageWriter.Ack(failure.CommandId!.Value, AckStatus.Rejected, failure.Reason));
                break;

            case ParseFailureKind.UnknownType when IsAuthenticated:
                SendError(ErrorCode.UnknownType, "Unsupported message type");
                break;

            case ParseFailureKind.Malformed when IsAuthenticated:
                SendError(ErrorCode.Malformed, "Malformed message");
                break;

            default:
                // Before authentication anything but a valid hello/pair ends the connection.
                SendError(ErrorCode.NotAuthenticated, "Not authenticated");
                RequestClose(CloseCodes.Unauthenticated, "Not authenticated");
                return;
        }

        if (++_protocolErrors > _limits.MaxProtocolErrors)
        {
            RequestClose(CloseCodes.Overload, "Too many protocol errors");
        }
    }

    private void HandleBeforeHello(ClientMessage message)
    {
        if (message is not HelloMessage hello)
        {
            SendError(ErrorCode.NotAuthenticated, "Not authenticated");
            RequestClose(CloseCodes.Unauthenticated, "Not authenticated");
            return;
        }

        _deviceName = hello.DeviceName;
        if (hello.Token is { } token)
        {
            if (_pairing.Authenticate(token) is { } device)
            {
                Authenticate(device);
                return;
            }

            // An invalid token reveals nothing else and gets no state (spec 41.7).
            LogAuthenticationRejected(_logger, Id);
            SendError(ErrorCode.NotAuthenticated, "Not authenticated");
            RequestClose(CloseCodes.Unauthenticated, "Not authenticated");
            return;
        }

        if (!_pairing.IsWindowOpen)
        {
            SendError(ErrorCode.PairUnavailable, "Pairing is not open on the PC");
            RequestClose(CloseCodes.Unauthenticated, "Pairing unavailable");
            return;
        }

        Volatile.Write(ref _pairRequiredAt, _time.GetTimestamp());
        Volatile.Write(ref _phase, StateAwaitPair);
        EnqueueControl(ServerMessageWriter.PairRequired());
    }

    private void HandlePairing(ClientMessage message)
    {
        if (message is not PairMessage pair)
        {
            SendError(ErrorCode.NotAuthenticated, "Not authenticated");
            RequestClose(CloseCodes.Unauthenticated, "Not authenticated");
            return;
        }

        var result = _pairing.TryPair(pair.Code, _deviceName);
        switch (result.Outcome)
        {
            case PairOutcome.Paired:
                EnqueueControl(ServerMessageWriter.Paired(_identity.ServerId, result.Token!));
                Authenticate(result.Device!);
                LogPaired(_logger, Id, result.Device!.Id);
                break;
            case PairOutcome.Invalid:
                SendError(ErrorCode.PairInvalid, "Invalid pairing code");
                break;
            case PairOutcome.Locked:
                SendError(ErrorCode.PairLocked, "Too many attempts; reopen pairing on the PC");
                RequestClose(CloseCodes.Unauthenticated, "Pairing locked");
                break;
            default:
                SendError(ErrorCode.PairUnavailable, "Pairing is not open on the PC");
                RequestClose(CloseCodes.Unauthenticated, "Pairing unavailable");
                break;
        }
    }

    private void HandleAuthenticated(ClientMessage message, long receivedAt)
    {
        switch (message)
        {
            case PingMessage ping:
                Volatile.Write(ref _lastPingAt, receivedAt);
                EnqueueControl(ServerMessageWriter.Pong(ping.Id, ping.T));
                break;

            case CommandMessage command:
                _monitor?.Received(_deviceName, command);
                if (_gate.Admit(command) is { } rejection)
                {
                    _monitor?.Result(command.Id, AckStatus.Rejected, rejection);
                    EnqueueControl(ServerMessageWriter.Ack(command.Id, AckStatus.Rejected, rejection));
                    break;
                }

                _executor.Submit(Id, command, receivedAt);
                break;

            default:
                // hello/pair after authentication is a protocol error.
                SendError(ErrorCode.Malformed, "Unexpected message");
                break;
        }
    }

    private void Authenticate(TrustedDevice device)
    {
        DeviceId = device.Id;
        Volatile.Write(ref _lastPingAt, _time.GetTimestamp());
        LeaveUnauthenticatedSlot();

        // Releases the previous controller's input before this one can act (spec 41.7).
        _registry.Activate(this);
        Volatile.Write(ref _phase, StateAuthenticated);

        EnqueueControl(ServerMessageWriter.Welcome(new WelcomeInfo(
            _identity.ServerName,
            _identity.ServerVersion,
            _identity.ServerId,
            _store.SessionId,
            Id,
            _identity.ChatKey)));

        _needsFull = true;
        _store.Changed += OnStateChanged;
        if (_activity is not null)
        {
            _activity.Changed += OnActivityChanged;
        }
        _signal.Release();
        LogAuthenticated(_logger, Id, device.Id);
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                while (_control.TryDequeue(out var frame))
                {
                    Interlocked.Decrement(ref _controlCount);
                    await SendAsync(frame, cancellationToken).ConfigureAwait(false);
                }

                if (Volatile.Read(ref _closeRequest) is { } close)
                {
                    await CloseOutputAsync(close).ConfigureAwait(false);
                    return;
                }

                if (!IsAuthenticated)
                {
                    continue;
                }

                if (_needsFull)
                {
                    _needsFull = false;
                    Interlocked.Exchange(ref _stateReady, 0);
                    var (snapshot, seq) = _store.TakeFull();
                    await SendAsync(ServerMessageWriter.FullState(_store.SessionId, seq, snapshot), cancellationToken).ConfigureAwait(false);
                    _activityCanSend = true;
                    LogStateSent(_logger, Id, seq, partial: false);
                }
                else if (Interlocked.Exchange(ref _stateReady, 0) == 1 && _store.TakeDelta() is { } delta)
                {
                    await SendAsync(ServerMessageWriter.DeltaState(_store.SessionId, delta.Seq, delta.Delta), cancellationToken).ConfigureAwait(false);
                    LogStateSent(_logger, Id, delta.Seq, partial: true);
                }

                Interlocked.Exchange(ref _activitySignaled, 0);
                if (_activityCanSend && _activity?.Read(_activityStream, _activitySeq) is { } activity)
                {
                    await SendAsync(ServerMessageWriter.Activity(_store.SessionId, activity), cancellationToken).ConfigureAwait(false);
                    _activityStream = activity.StreamId;
                    _activitySeq = activity.Events.Count > 0 ? activity.Events[^1].Seq : 0;
                    if (_activitySeq < activity.LatestSeq)
                    {
                        OnActivityChanged();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Lifetime ended.
        }
        catch (WebSocketException ex)
        {
            LogSocketError(_logger, Id, ex.WebSocketErrorCode);
            _lifetime?.Cancel();
        }
    }

    private async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_limits.SendTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await _socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Slow client: release input and drop the connection (spec 41.6).
            LogSlowClient(_logger, Id);
            Volatile.Write(ref _closeRequest, new CloseRequest(CloseCodes.Overload, "Slow client"));
            _executor.CancelConnection(Id, CancelReason.Disconnected);
            _lifetime?.Cancel();
            throw;
        }
    }

    private async Task CloseOutputAsync(CloseRequest close)
    {
        // Input of this connection is released before the close frame goes out.
        _executor.CancelConnection(Id, ReasonFor(close.Code));

        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(_limits.SendTimeout, _time);
            try
            {
                await _socket.CloseOutputAsync((WebSocketCloseStatus)close.Code, close.Description, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
                // The peer is gone or too slow; aborting below is enough.
            }
        }

        // Give the peer a short window to acknowledge, then end the receive loop.
        _lifetime?.CancelAfter(_limits.CloseHandshakeTimeout);
    }

    private async Task WaitForSendLoopAsync(Task sendLoop, CancellationTokenSource lifetime)
    {
        try
        {
            await sendLoop.WaitAsync(_limits.CloseHandshakeTimeout + _limits.SendTimeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// After our close frame, keep reading (and discarding) until the peer answers with its own
    /// close frame. Aborting earlier sends a TCP reset that can destroy the queued error and close
    /// frame before the client has read them.
    /// </summary>
    private async Task DrainUntilPeerClosesAsync()
    {
        if (_socket.State != WebSocketState.CloseSent)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(_limits.CloseHandshakeTimeout, _time);
        var scratch = new byte[1024];
        try
        {
            while (_socket.State == WebSocketState.CloseSent)
            {
                var result = await _socket.ReceiveAsync(scratch.AsMemory(), timeout.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
        {
            // Peer did not complete the handshake in time; Shutdown aborts the socket.
        }
    }

    private void Shutdown()
    {
        _store.Changed -= OnStateChanged;
        if (_activity is not null)
        {
            _activity.Changed -= OnActivityChanged;
        }
        _debounceTimer.Dispose();
        LeaveUnauthenticatedSlot();
        var reason = Volatile.Read(ref _closeRequest) is { } close ? ReasonFor(close.Code) : CancelReason.Disconnected;
        _registry.Deactivate(this, reason);

        if (_socket.State != WebSocketState.Closed)
        {
            _socket.Abort();
        }

        LogClosed(_logger, Id, Volatile.Read(ref _closeRequest)?.Code ?? 0, Volatile.Read(ref _closeRequest)?.Description ?? "Transport ended");
    }

    private void CheckDeadlines()
    {
        if (Volatile.Read(ref _closeRequest) is not null)
        {
            return;
        }

        var messageStartedAt = Volatile.Read(ref _messageStartedAt);
        if (messageStartedAt != 0 && _time.GetElapsedTime(messageStartedAt) > _limits.MessageAssemblyTimeout)
        {
            RequestClose((int)WebSocketCloseStatus.PolicyViolation, "Message assembly timeout");
            return;
        }

        switch (Volatile.Read(ref _phase))
        {
            case StateAwaitHello when _time.GetElapsedTime(_startedAt) > _limits.HelloTimeout:
                RequestClose(CloseCodes.Unauthenticated, "Hello timeout");
                break;

            case StateAwaitPair when _time.GetElapsedTime(Volatile.Read(ref _pairRequiredAt)) > PairingService.WindowDuration
                                     || !_pairing.IsWindowOpen:
                SendError(ErrorCode.PairUnavailable, "Pairing window closed");
                RequestClose(CloseCodes.Unauthenticated, "Pairing window closed");
                break;

            case StateAuthenticated when _time.GetElapsedTime(Volatile.Read(ref _lastPingAt)) > _limits.HeartbeatTimeout:
                // Release first: the network may be gone and the close frame may never arrive.
                _executor.CancelConnection(Id, CancelReason.Watchdog);
                RequestClose(CloseCodes.HeartbeatTimeout, "Heartbeat timeout");
                break;
        }
    }

    private bool CountIncomingMessage(long now)
    {
        if (_time.GetElapsedTime(_rateWindowStart, now) >= TimeSpan.FromSeconds(1))
        {
            _rateWindowStart = now;
            _rateWindowCount = 0;
        }

        return ++_rateWindowCount <= _limits.MaxMessagesPerSecond;
    }

    private void OnActivityChanged()
    {
        if (Volatile.Read(ref _closeRequest) is null && Interlocked.Exchange(ref _activitySignaled, 1) == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (ObjectDisposedException)
            {
                // A reader callback already in flight when Shutdown unsubscribed is harmless.
            }
        }
    }

    private void OnStateChanged(bool urgent)
    {
        if (urgent)
        {
            Interlocked.Exchange(ref _stateReady, 1);
            _signal.Release();
            return;
        }

        if (Interlocked.Exchange(ref _debouncePending, 1) == 0)
        {
            _debounceTimer.Change(_limits.StateDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnDebounceElapsed()
    {
        Interlocked.Exchange(ref _debouncePending, 0);
        Interlocked.Exchange(ref _stateReady, 1);
        _signal.Release();
    }

    private void SendError(ErrorCode code, string message) => EnqueueControl(ServerMessageWriter.Error(code, message));

    private void LeaveUnauthenticatedSlot()
    {
        if (!_leftUnauthenticated)
        {
            _leftUnauthenticated = true;
            _registry.LeaveUnauthenticated();
        }
    }

    private static CancelReason ReasonFor(int closeCode) => closeCode switch
    {
        CloseCodes.Replaced => CancelReason.ClientReplaced,
        CloseCodes.Revoked => CancelReason.Revoked,
        CloseCodes.HeartbeatTimeout => CancelReason.Watchdog,
        _ => CancelReason.Disconnected,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {ConnectionId} authenticated as device {DeviceId}")]
    private static partial void LogAuthenticated(ILogger logger, Guid connectionId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {ConnectionId} paired new device {DeviceId}")]
    private static partial void LogPaired(ILogger logger, Guid connectionId, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {ConnectionId} closed with code {CloseCode}: {Description}")]
    private static partial void LogClosed(ILogger logger, Guid connectionId, int closeCode, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection {ConnectionId} socket error {ErrorCode}")]
    private static partial void LogSocketError(ILogger logger, Guid connectionId, WebSocketError errorCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection {ConnectionId} dropped as slow client")]
    private static partial void LogSlowClient(ILogger logger, Guid connectionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Connection {ConnectionId} sent state seq {Seq} (partial: {Partial})")]
    private static partial void LogStateSent(ILogger logger, Guid connectionId, long seq, bool partial);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection {ConnectionId} rejected a stored pairing token")]
    private static partial void LogAuthenticationRejected(ILogger logger, Guid connectionId);

    private sealed record CloseRequest(int Code, string Description);
}
