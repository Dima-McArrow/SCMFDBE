namespace Csmfd.Protocol;

/// <summary>Contract v2 constants (spec sections 41.6, 41.10, 41.12).</summary>
public static class ProtocolLimits
{
    public const int Version = 2;
    public const int Port = 47823;
    public const string EndpointPath = "/csmfd";

    /// <summary>Total size of one logical message across all WebSocket fragments.</summary>
    public const int MaxMessageBytes = 16 * 1024;
    public const int MaxJsonDepth = 8;

    /// <summary>Client, device, ship and target names, in Unicode scalar values.</summary>
    public const int MaxNameScalars = 128;
    public const int MaxErrorMessageScalars = 256;
    public const int MaxPingIdScalars = 64;

    public const int MinHoldMs = 50;
    public const int MaxHoldMs = 10_000;

    public const int MinChatScalars = 2;
    public const int MaxChatScalars = 64;

    public const int PairCodeLength = 6;

    /// <summary>2^53 - 1: largest integer the iPad side can represent exactly.</summary>
    public const long MaxSafeInteger = 9_007_199_254_740_991;
}

/// <summary>WebSocket close codes (spec 41.10).</summary>
public static class CloseCodes
{
    public const int Replaced = 4001;
    public const int UnsupportedVersion = 4002;
    public const int Unauthenticated = 4003;
    public const int HeartbeatTimeout = 4004;
    public const int Overload = 4005;
    public const int Revoked = 4006;
}

/// <summary>Values of the "type" field.</summary>
public static class MessageTypes
{
    public const string Hello = "hello";
    public const string Pair = "pair";
    public const string PairRequired = "pairRequired";
    public const string Paired = "paired";
    public const string Welcome = "welcome";
    public const string Key = "key";
    public const string Wheel = "wheel";
    public const string Chat = "chat";
    public const string ReleaseAll = "releaseAll";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Ack = "ack";
    public const string State = "state";
    public const string Activity = "activity";
    public const string Error = "error";
}
