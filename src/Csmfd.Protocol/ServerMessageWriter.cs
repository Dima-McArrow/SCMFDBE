using System.Buffers;
using System.Globalization;
using System.Text.Json;

namespace Csmfd.Protocol;

/// <summary>
/// Serializes server → client messages (contract v2). Hand-written on Utf8JsonWriter so that
/// "key absent" and "key = null" stay distinct in state deltas, and so invariants of spec 41.8–41.10
/// are enforced at the single place where bytes leave the server. Violations throw: they are bugs.
/// </summary>
public static class ServerMessageWriter
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static byte[] Ack(Guid commandId, AckStatus status, RejectReason? reason = null)
    {
        if ((status == AckStatus.Rejected) != reason.HasValue)
        {
            throw new ArgumentException("A reason is required for rejected acks and forbidden otherwise.", nameof(reason));
        }

        return Write(MessageTypes.Ack, writer =>
        {
            writer.WriteString("id", commandId);
            writer.WriteString("status", status.ToWire());
            if (reason is { } value)
            {
                writer.WriteString("reason", value.ToWire());
            }
        });
    }

    public static byte[] Error(ErrorCode code, string message, Guid? commandId = null, CancelReason? reason = null)
    {
        RequireText(message, ProtocolLimits.MaxErrorMessageScalars, nameof(message));

        var refersToCommand = code is ErrorCode.InputCancelled or ErrorCode.AutoReleased;
        if (refersToCommand != commandId.HasValue || refersToCommand != reason.HasValue)
        {
            throw new ArgumentException("commandId and reason are required for input_cancelled/auto_released only.", nameof(code));
        }

        return Write(MessageTypes.Error, writer =>
        {
            writer.WriteString("code", code.ToWire());
            if (commandId is { } id)
            {
                writer.WriteString("commandId", id);
            }

            if (reason is { } value)
            {
                writer.WriteString("reason", value.ToWire());
            }

            writer.WriteString("message", message);
        });
    }

    public static byte[] Pong(string pingId, long t)
    {
        RequireText(pingId, ProtocolLimits.MaxPingIdScalars, nameof(pingId));

        return Write(MessageTypes.Pong, writer =>
        {
            writer.WriteString("id", pingId);
            writer.WriteNumber("t", t);
        });
    }

    public static byte[] PairRequired() => Write(MessageTypes.PairRequired, static _ => { });

    public static byte[] Paired(Guid serverId, string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (!TextRules.IsTokenFormat(token))
        {
            throw new ArgumentException("Token must be 32 bytes in base64url without padding.", nameof(token));
        }

        return Write(MessageTypes.Paired, writer =>
        {
            writer.WriteString("serverId", serverId);
            writer.WriteString("token", token);
        });
    }

    public static byte[] Welcome(WelcomeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        RequireText(info.Server, ProtocolLimits.MaxNameScalars, nameof(info));
        RequireText(info.ServerVersion, ProtocolLimits.MaxNameScalars, nameof(info));

        return Write(MessageTypes.Welcome, writer =>
        {
            writer.WriteString("server", info.Server);
            writer.WriteString("serverVersion", info.ServerVersion);
            writer.WriteString("serverId", info.ServerId);
            writer.WriteString("sessionId", info.SessionId);
            writer.WriteString("connectionId", info.ConnectionId);
            writer.WriteString("chatKey", info.ChatKey.ToWire());
        });
    }

    public static byte[] Activity(Guid sessionId, ActivityPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        RequireCounter(page.LatestSeq, nameof(page));
        if (page.FirstSeq < 1 || page.FirstSeq > page.LatestSeq + 1 || page.Events.Count > ActivityLimits.PageSize
            || (page.Events.Count == 0 && page.LatestSeq != 0))
        {
            throw new ArgumentException("Invalid activity page bounds.", nameof(page));
        }

        return Write(MessageTypes.Activity, writer =>
        {
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("streamId", page.StreamId);
            WriteTimestamp(writer, "startedAt", page.StartedAt);
            writer.WriteNumber("firstSeq", page.FirstSeq);
            writer.WriteNumber("latestSeq", page.LatestSeq);
            writer.WriteBoolean("ended", page.Ended);
            writer.WriteStartArray("events");
            long previous = 0;
            foreach (var item in page.Events)
            {
                if (item.Seq < page.FirstSeq || item.Seq > page.LatestSeq || (previous != 0 && item.Seq != previous + 1)
                    || !ActivityKinds.Contains(item.Kind))
                {
                    throw new ArgumentException("Invalid activity event.", nameof(page));
                }

                if (!TextRules.IsValidText(item.Text, 0, ActivityLimits.TextScalars))
                {
                    throw new ArgumentException("Invalid activity text.", nameof(page));
                }
                if (item.ObjectiveId is { } objective)
                {
                    RequireText(objective, ActivityLimits.ObjectiveScalars, nameof(page));
                }

                if (item.Kind == "reward" ? item.Amount is not (> 0 and <= 1_000_000_000) || item.Currency is not ("aUEC" or "UEC")
                    : item.Amount is not null || item.Currency is not null)
                {
                    throw new ArgumentException("Invalid reward amount/currency.", nameof(page));
                }

                var isParty = PartyActivity.IsKind(item.Kind);
                if (isParty && (!PartyActivity.IsValidText(item.Kind, item.Text)
                    || item.MissionId is not null || item.ObjectiveId is not null))
                {
                    throw new ArgumentException("Invalid party observation.", nameof(page));
                }

                previous = item.Seq;
                if (item.MissionId == Guid.Empty
                    || (!isParty && item.Kind is not ("reward" or "sessionEnded") && item.MissionId is null))
                {
                    throw new ArgumentException("A contract activity requires a nonzero mission ID.", nameof(page));
                }

                writer.WriteStartObject();
                writer.WriteNumber("seq", item.Seq);
                writer.WriteString("kind", item.Kind);
                WriteTimestamp(writer, "at", item.At);
                writer.WriteString("text", item.Text);

                if (item.MissionId is { } mission)
                {
                    writer.WriteString("missionId", mission);
                }

                if (item.ObjectiveId is { } objectiveId)
                {
                    writer.WriteString("objectiveId", objectiveId);
                }

                if (item.Amount is { } amount)
                {
                    writer.WriteNumber("amount", amount);
                    writer.WriteString("currency", item.Currency);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    private static readonly HashSet<string> ActivityKinds = new(StringComparer.Ordinal)
    {
        "reward", "contractAccepted", "contractComplete", "contractWithdrawn", "contractFailed",
        "newObjective", "objectiveComplete", "sessionEnded",
        "partyCreated", "partyJoined", "partyMemberJoined", "partyMemberLeft", "partyLeaderChanged", "partyDisbanded",
    };

    public static byte[] FullState(Guid sessionId, long seq, StateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireCounter(seq, nameof(seq));

        return Write(MessageTypes.State, writer =>
        {
            WriteStateHeader(writer, sessionId, seq, partial: false);
            WriteGame(writer, snapshot.Game);
            WriteLog(writer, snapshot.Log);
            WriteShip(writer, snapshot.Ship);
            WriteShipContext(writer, snapshot.ShipContext);
            WriteQtTarget(writer, snapshot.QtTarget);
            WriteHangarRequest(writer, snapshot.HangarRequest);
        });
    }

    public static byte[] DeltaState(Guid sessionId, long seq, StateDelta delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        RequireCounter(seq, nameof(seq));
        if (delta.IsEmpty)
        {
            throw new ArgumentException("A state delta must change at least one key.", nameof(delta));
        }

        return Write(MessageTypes.State, writer =>
        {
            WriteStateHeader(writer, sessionId, seq, partial: true);
            if (delta.Game is { } game)
            {
                WriteGame(writer, game);
            }

            if (delta.Log is { } log)
            {
                WriteLog(writer, log);
            }

            if (delta.Ship is { } ship)
            {
                WriteShip(writer, ship.Value);
            }

            if (delta.ShipContext is { } shipContext)
            {
                WriteShipContext(writer, shipContext);
            }

            if (delta.QtTarget is { } qtTarget)
            {
                WriteQtTarget(writer, qtTarget);
            }

            if (delta.HangarRequest is { } hangarRequest)
            {
                WriteHangarRequest(writer, hangarRequest);
            }
        });
    }

    private static void WriteStateHeader(Utf8JsonWriter writer, Guid sessionId, long seq, bool partial)
    {
        writer.WriteString("sessionId", sessionId);
        writer.WriteNumber("seq", seq);
        writer.WriteBoolean("partial", partial);
    }

    private static void WriteGame(Utf8JsonWriter writer, GameState game)
    {
        ArgumentNullException.ThrowIfNull(game);
        RequireOptionalText(game.Build, nameof(game));

        writer.WriteStartObject("game");
        writer.WriteBoolean("running", game.Running);
        writer.WriteBoolean("focused", game.Focused);
        WriteOptionalString(writer, "build", game.Build);
        writer.WriteEndObject();
    }

    private static void WriteLog(Utf8JsonWriter writer, LogState log)
    {
        ArgumentNullException.ThrowIfNull(log);

        writer.WriteStartObject("log");
        writer.WriteString("status", ToWire(log.Status));
        writer.WriteString("generation", log.Generation);
        WriteTimestamp(writer, "observedAt", log.ObservedAt);
        writer.WriteEndObject();
    }

    private static void WriteShip(Utf8JsonWriter writer, ShipState? ship)
    {
        if (ship is null)
        {
            writer.WriteNull("ship");
            return;
        }

        RequireText(ship.Name, ProtocolLimits.MaxNameScalars, nameof(ship));
        RequireOptionalText(ship.Model, nameof(ship));
        RequireOptionalText(ship.EntityId, nameof(ship));
        RequireOptionalText(ship.ManufacturerPrefix, nameof(ship));

        writer.WriteStartObject("ship");
        writer.WriteString("name", ship.Name);
        WriteOptionalString(writer, "model", ship.Model);
        WriteOptionalString(writer, "entityId", ship.EntityId);
        WriteOptionalString(writer, "manufacturerPrefix", ship.ManufacturerPrefix);
        writer.WriteString("confidence", ToWire(ship.Confidence));
        WriteTimestamp(writer, "observedAt", ship.ObservedAt);
        writer.WriteEndObject();
    }

    private static void WriteShipContext(Utf8JsonWriter writer, ShipContextState context)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireCounter(context.Revision, nameof(context));
        RequireOptionalText(context.ManufacturerPrefix, nameof(context));

        writer.WriteStartObject("shipContext");
        writer.WriteNumber("revision", context.Revision);
        writer.WriteString("status", ToWire(context.Status));
        writer.WriteString("reason", ToWire(context.Reason));
        WriteOptionalGuid(writer, "contextId", context.ContextId);
        WriteOptionalString(writer, "manufacturerPrefix", context.ManufacturerPrefix);
        WriteTimestamp(writer, "observedAt", context.ObservedAt);
        writer.WriteEndObject();
    }

    private static void WriteQtTarget(Utf8JsonWriter writer, QtTargetState target)
    {
        ArgumentNullException.ThrowIfNull(target);
        RequireOptionalText(target.InternalName, nameof(target));
        RequireOptionalText(target.DisplayName, nameof(target));

        // Spec 41.8: unknown has no names or time and is always stale; destination needs a name.
        if (target.Mode == QtMode.Unknown
            && (target.InternalName is not null || target.DisplayName is not null
                || target.Confidence != Confidence.Stale || target.ObservedAt is not null))
        {
            throw new ArgumentException("Unknown QT target must have null names/time and stale confidence.", nameof(target));
        }

        if (target.Mode == QtMode.Destination && target.InternalName is null && target.DisplayName is null)
        {
            throw new ArgumentException("Destination QT target requires internalName or displayName.", nameof(target));
        }

        writer.WriteStartObject("qtTarget");
        writer.WriteString("mode", ToWire(target.Mode));
        WriteOptionalString(writer, "internalName", target.InternalName);
        WriteOptionalString(writer, "displayName", target.DisplayName);
        writer.WriteString("confidence", ToWire(target.Confidence));
        WriteTimestamp(writer, "observedAt", target.ObservedAt);
        writer.WriteEndObject();
    }

    private static void WriteHangarRequest(Utf8JsonWriter writer, HangarRequestState request)
    {
        ArgumentNullException.ThrowIfNull(request);

        writer.WriteStartObject("hangarRequest");
        writer.WriteString("status", ToWire(request.Status));
        WriteOptionalGuid(writer, "commandId", request.CommandId);
        WriteOptionalGuid(writer, "contextId", request.ContextId);
        WriteTimestamp(writer, "since", request.Since);
        WriteTimestamp(writer, "updatedAt", request.UpdatedAt);
        writer.WriteEndObject();
    }

    private static byte[] Write(string type, Action<Utf8JsonWriter> writeBody)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", ProtocolLimits.Version);
            writer.WriteString("type", type);
            writeBody(writer);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > ProtocolLimits.MaxMessageBytes)
        {
            throw new InvalidOperationException("Outbound message exceeds the protocol size limit.");
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteOptionalString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteOptionalGuid(Utf8JsonWriter writer, string name, Guid? value)
    {
        if (value is { } guid)
        {
            writer.WriteString(name, guid);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>ISO 8601 UTC with milliseconds, e.g. 2026-10-26T15:56:52.708Z.</summary>
    private static void WriteTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value)
    {
        if (value is { } timestamp)
        {
            writer.WriteString(name, timestamp.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    private static void RequireText(string value, int maxScalars, string paramName)
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        if (!TextRules.IsValidText(value, 1, maxScalars))
        {
            throw new ArgumentException("Text is empty, too long or contains control characters.", paramName);
        }
    }

    /// <summary>Unknown values are sent as null, never as empty strings (spec 41.8).</summary>
    private static void RequireOptionalText(string? value, string paramName)
    {
        if (value is not null)
        {
            RequireText(value, ProtocolLimits.MaxNameScalars, paramName);
        }
    }

    private static void RequireCounter(long value, string paramName)
    {
        if (value is < 0 or > ProtocolLimits.MaxSafeInteger)
        {
            throw new ArgumentOutOfRangeException(paramName, "Counter must be within 0..2^53-1.");
        }
    }

    private static string ToWire(Confidence value) => value switch
    {
        Confidence.Confirmed => "confirmed",
        Confidence.Assumed => "assumed",
        Confidence.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWire(LogStatus value) => value switch
    {
        LogStatus.Ready => "ready",
        LogStatus.Missing => "missing",
        LogStatus.Unreadable => "unreadable",
        LogStatus.CatchingUp => "catchingUp",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWire(ShipContextStatus value) => value switch
    {
        ShipContextStatus.Unknown => "unknown",
        ShipContextStatus.Assumed => "assumed",
        ShipContextStatus.NoShip => "noShip",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWire(ShipContextReason value) => value switch
    {
        ShipContextReason.Startup => "startup",
        ShipContextReason.ChannelJoined => "channelJoined",
        ShipContextReason.ChannelLeft => "channelLeft",
        ShipContextReason.GameEnded => "gameEnded",
        ShipContextReason.LogReset => "logReset",
        ShipContextReason.VehicleDestroyed => "vehicleDestroyed",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWire(QtMode value) => value switch
    {
        QtMode.Unknown => "unknown",
        QtMode.FreeMode => "freeMode",
        QtMode.Destination => "destination",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWire(HangarRequestStatus value) => value switch
    {
        HangarRequestStatus.Idle => "idle",
        HangarRequestStatus.Pending => "pending",
        HangarRequestStatus.Answered => "answered",
        HangarRequestStatus.Timeout => "timeout",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
