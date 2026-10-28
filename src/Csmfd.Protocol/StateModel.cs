namespace Csmfd.Protocol;

public enum Confidence
{
    Confirmed,
    Assumed,
    Stale,
}

public enum LogStatus
{
    Ready,
    Missing,
    Unreadable,
    CatchingUp,
}

public enum ShipContextStatus
{
    Unknown,
    Assumed,
    NoShip,
}

public enum ShipContextReason
{
    Startup,
    ChannelJoined,
    ChannelLeft,
    GameEnded,
    LogReset,

    /// <summary>The current (or just left) ship was destroyed with the player aboard; the iPad resets its Deck.</summary>
    VehicleDestroyed,
}

public enum QtMode
{
    Unknown,
    FreeMode,
    Destination,
}

public enum HangarRequestStatus
{
    Idle,
    Pending,
    Answered,
    Timeout,
}

/// <summary>Game process as seen by the server. Build is null when unknown.</summary>
public sealed record GameState(bool Running, bool Focused, string? Build);

/// <summary>Health of the Game.log tailer; Ready does not imply any telemetry is available.</summary>
public sealed record LogState(LogStatus Status, Guid Generation, DateTimeOffset? ObservedAt);

/// <summary>Currently assumed controlled ship. The whole object is null when there is no ship.</summary>
public sealed record ShipState(
    string Name,
    string? Model,
    string? EntityId,
    string? ManufacturerPrefix,
    Confidence Confidence,
    DateTimeOffset ObservedAt);

/// <summary>Ship lifecycle; Revision grows on every real transition (spec 41.8, 41.9).</summary>
public sealed record ShipContextState(
    long Revision,
    ShipContextStatus Status,
    ShipContextReason Reason,
    Guid? ContextId,
    string? ManufacturerPrefix,
    DateTimeOffset? ObservedAt);

public sealed record QtTargetState(
    QtMode Mode,
    string? InternalName,
    string? DisplayName,
    Confidence Confidence,
    DateTimeOffset? ObservedAt)
{
    public static QtTargetState Unknown { get; } = new(QtMode.Unknown, null, null, Confidence.Stale, null);
}

public sealed record HangarRequestState(
    HangarRequestStatus Status,
    Guid? CommandId,
    Guid? ContextId,
    DateTimeOffset? Since,
    DateTimeOffset? UpdatedAt)
{
    public static HangarRequestState Idle { get; } = new(HangarRequestStatus.Idle, null, null, null, null);
}

/// <summary>Complete state sent as the first state message of every connection.</summary>
public sealed record StateSnapshot(
    GameState Game,
    LogState Log,
    ShipState? Ship,
    ShipContextState ShipContext,
    QtTargetState QtTarget,
    HangarRequestState HangarRequest);

/// <summary>Explicit change of the nullable ship slot: Value == null clears the ship.</summary>
public sealed record ShipUpdate(ShipState? Value);

/// <summary>
/// Changed top-level keys only. A null property means "unchanged" and is omitted on the wire;
/// clearing the ship is expressed with <see cref="ShipUpdate"/> so null stays unambiguous.
/// </summary>
public sealed record StateDelta
{
    public GameState? Game { get; init; }

    public LogState? Log { get; init; }

    public ShipUpdate? Ship { get; init; }

    public ShipContextState? ShipContext { get; init; }

    public QtTargetState? QtTarget { get; init; }

    public HangarRequestState? HangarRequest { get; init; }

    public bool IsEmpty =>
        Game is null && Log is null && Ship is null && ShipContext is null && QtTarget is null && HangarRequest is null;
}

/// <summary>Fields of the welcome message (spec 41.7).</summary>
public sealed record WelcomeInfo(
    string Server,
    string ServerVersion,
    Guid ServerId,
    Guid SessionId,
    Guid ConnectionId,
    ChatKey ChatKey);
