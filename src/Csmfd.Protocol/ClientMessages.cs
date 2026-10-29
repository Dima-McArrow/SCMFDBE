using System.Diagnostics.CodeAnalysis;

namespace Csmfd.Protocol;

/// <summary>Validated client → server message. Instances only come from <see cref="ClientMessageParser"/>.</summary>
public abstract record ClientMessage;

/// <summary>First message after TLS. Token is null for a device that is not paired yet.</summary>
public sealed record HelloMessage(string Client, string AppVersion, string DeviceName, string? Token) : ClientMessage;

public sealed record PairMessage(string Code) : ClientMessage;

public sealed record PingMessage(string Id, long T) : ClientMessage;

/// <summary>Messages that must receive exactly one terminal ack.</summary>
public abstract record CommandMessage(Guid Id) : ClientMessage;

/// <summary>Allowlisted key command. Ms is present only for <see cref="KeyPhase.Hold"/>.</summary>
public sealed record KeyCommand(Guid Id, ActionSpec Spec, KeyPhase Phase, int? Ms) : CommandMessage(Id);

/// <summary>One relative wheel step: Step is exactly +1 (increase) or -1 (decrease).</summary>
public sealed record WheelCommand(Guid Id, WheelActionSpec Spec, int Step) : CommandMessage(Id);

/// <summary>Structurally valid chat text; the emote allowlist is checked by the server.</summary>
public sealed record ChatCommand(Guid Id, string Text) : CommandMessage(Id);

public sealed record ReleaseAllCommand(Guid Id) : CommandMessage(Id);

public enum ParseFailureKind
{
    /// <summary>Reply with error "malformed".</summary>
    Malformed,

    /// <summary>Reply with error "unsupported_version" and close 4002.</summary>
    UnsupportedVersion,

    /// <summary>Reply with error "unknown_type"; the connection stays open.</summary>
    UnknownType,

    /// <summary>Command id is valid: reply with ack "rejected" and <see cref="ParseFailure.Reason"/>.</summary>
    CommandRejected,
}

/// <summary>Why a message was refused. Detail is for local logs and never echoes client input.</summary>
public sealed record ParseFailure(ParseFailureKind Kind, string Detail, Guid? CommandId = null, RejectReason? Reason = null);

public sealed class ParseResult
{
    private ParseResult(ClientMessage? message, ParseFailure? failure)
    {
        Message = message;
        Failure = failure;
    }

    public ClientMessage? Message { get; }

    public ParseFailure? Failure { get; }

    [MemberNotNullWhen(true, nameof(Message))]
    [MemberNotNullWhen(false, nameof(Failure))]
    public bool IsSuccess => Message is not null;

    public static ParseResult Success(ClientMessage message) => new(message, null);

    public static ParseResult Fail(ParseFailure failure) => new(null, failure);
}
