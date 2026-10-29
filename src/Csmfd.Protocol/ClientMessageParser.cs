using System.Text.Json;

namespace Csmfd.Protocol;

/// <summary>
/// Strict parser for client → server messages (contract v2, spec 41.6–41.12, 41.26).
/// Unknown fields are ignored except on wheel commands; a missing required field is never treated as null.
/// </summary>
public static class ClientMessageParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = ProtocolLimits.MaxJsonDepth,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    public static ParseResult Parse(ReadOnlyMemory<byte> utf8Json)
    {
        // The transport enforces the same limit across fragments; this is defense in depth.
        if (utf8Json.Length > ProtocolLimits.MaxMessageBytes)
        {
            return Malformed("message exceeds size limit");
        }

        try
        {
            using var document = JsonDocument.Parse(utf8Json, DocumentOptions);
            return ParseRoot(document.RootElement);
        }
        catch (JsonException)
        {
            // Invalid JSON/UTF-8, duplicate property, depth limit, trailing comma or comment.
            return Malformed("invalid JSON");
        }
        catch (InvalidOperationException)
        {
            // String content that cannot be transcoded (e.g. a lone surrogate escape).
            return Malformed("invalid string content");
        }
    }

    private static ParseResult ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Malformed("root must be an object");
        }

        if (!root.TryGetProperty("v", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version))
        {
            return Malformed("missing or invalid 'v'");
        }

        if (version != ProtocolLimits.Version)
        {
            return ParseResult.Fail(new ParseFailure(ParseFailureKind.UnsupportedVersion, "unsupported protocol version"));
        }

        if (!TryGetString(root, "type", out var type))
        {
            return Malformed("missing or invalid 'type'");
        }

        return type switch
        {
            MessageTypes.Hello => ParseHello(root),
            MessageTypes.Pair => ParsePair(root),
            MessageTypes.Ping => ParsePing(root),
            MessageTypes.Key => ParseCommand(root, ParseKey),
            MessageTypes.Wheel => ParseCommand(root, ParseWheel),
            MessageTypes.Chat => ParseCommand(root, ParseChat),
            MessageTypes.ReleaseAll => ParseCommand(root, static (_, id) => ParseResult.Success(new ReleaseAllCommand(id))),
            _ => ParseResult.Fail(new ParseFailure(ParseFailureKind.UnknownType, "unsupported message type")),
        };
    }

    private static ParseResult ParseHello(JsonElement root)
    {
        if (!TryGetName(root, "client", out var client)
            || !TryGetName(root, "appVersion", out var appVersion)
            || !TryGetName(root, "deviceName", out var deviceName))
        {
            return Malformed("invalid hello fields");
        }

        // "token" is required; an unpaired device sends an explicit null.
        if (!root.TryGetProperty("token", out var tokenElement))
        {
            return Malformed("missing 'token'");
        }

        string? token = null;
        if (tokenElement.ValueKind == JsonValueKind.String)
        {
            token = tokenElement.GetString()!;
            if (!TextRules.IsTokenFormat(token))
            {
                return Malformed("invalid token format");
            }
        }
        else if (tokenElement.ValueKind != JsonValueKind.Null)
        {
            return Malformed("invalid 'token' type");
        }

        return ParseResult.Success(new HelloMessage(client, appVersion, deviceName, token));
    }

    private static ParseResult ParsePair(JsonElement root)
    {
        if (!TryGetString(root, "code", out var code) || !TextRules.IsPairCodeFormat(code))
        {
            return Malformed("invalid pairing code format");
        }

        return ParseResult.Success(new PairMessage(code));
    }

    private static ParseResult ParsePing(JsonElement root)
    {
        if (!TryGetString(root, "id", out var id)
            || !TextRules.IsValidText(id, 1, ProtocolLimits.MaxPingIdScalars))
        {
            return Malformed("invalid ping 'id'");
        }

        if (!root.TryGetProperty("t", out var timeElement)
            || timeElement.ValueKind != JsonValueKind.Number
            || !timeElement.TryGetInt64(out var time))
        {
            return Malformed("invalid ping 't'");
        }

        return ParseResult.Success(new PingMessage(id, time));
    }

    private static ParseResult ParseCommand(JsonElement root, Func<JsonElement, Guid, ParseResult> parseBody)
    {
        // Without a valid id the failure cannot be correlated, so it is a protocol error, not an ack.
        if (!TryGetString(root, "id", out var rawId) || !Guid.TryParseExact(rawId, "D", out var id))
        {
            return Malformed("missing or invalid command 'id'");
        }

        return parseBody(root, id);
    }

    private static ParseResult ParseKey(JsonElement root, Guid id)
    {
        if (!TryGetString(root, "action", out var action)
            || !TryGetString(root, "phase", out var rawPhase)
            || !TryGetString(root, "key", out var key))
        {
            return Rejected(id, RejectReason.Invalid, "missing key fields");
        }

        if (!WireNames.TryParseKeyPhase(rawPhase, out var phase))
        {
            return Rejected(id, RejectReason.Invalid, "unknown phase");
        }

        if (!TryParseMods(root, out var mods))
        {
            return Rejected(id, RejectReason.Invalid, "invalid mods");
        }

        if (!TryParseMs(root, phase, out var ms))
        {
            return Rejected(id, RejectReason.Invalid, "invalid ms");
        }

        if (!KeyNames.IsKnown(key))
        {
            return Rejected(id, RejectReason.UnknownKey, "unknown key");
        }

        if (!ActionCatalog.TryGet(action, out var spec))
        {
            return Rejected(id, RejectReason.UnknownAction, "unknown action");
        }

        if (!string.Equals(spec.Key, key, StringComparison.Ordinal) || spec.Mods != mods)
        {
            return Rejected(id, RejectReason.Invalid, "key/mods do not match action");
        }

        if (!spec.AllowsPhase(phase))
        {
            return Rejected(id, RejectReason.Invalid, "phase not allowed for action");
        }

        return ParseResult.Success(new KeyCommand(id, spec, phase, ms));
    }

    private static ParseResult ParseWheel(JsonElement root, Guid id)
    {
        // No client-controlled binding, axis, delta, duration or batch size, including future-looking fields.
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is not ("v" or "type" or "id" or "action" or "step"))
            {
                return Rejected(id, RejectReason.Invalid, "unexpected wheel field");
            }
        }

        if (!TryGetString(root, "action", out var action)
            || !root.TryGetProperty("step", out var stepElement)
            || stepElement.ValueKind != JsonValueKind.Number
            || !stepElement.TryGetInt32(out var step)
            || step is not (-1 or 1))
        {
            return Rejected(id, RejectReason.Invalid, "invalid wheel fields");
        }

        if (!WheelActionCatalog.All.TryGetValue(action, out var spec))
        {
            return Rejected(id, RejectReason.UnknownAction, "unknown wheel action");
        }

        return ParseResult.Success(new WheelCommand(id, spec, step));
    }

    private static ParseResult ParseChat(JsonElement root, Guid id)
    {
        if (!TryGetString(root, "text", out var text)
            || !TextRules.IsValidText(text, ProtocolLimits.MinChatScalars, ProtocolLimits.MaxChatScalars)
            || !text.StartsWith('/'))
        {
            return Rejected(id, RejectReason.Invalid, "invalid chat text");
        }

        return ParseResult.Success(new ChatCommand(id, text));
    }

    /// <summary>"mods" is optional (default none); when present it must be an array of unique known names.</summary>
    private static bool TryParseMods(JsonElement root, out Modifiers mods)
    {
        mods = Modifiers.None;
        if (!root.TryGetProperty("mods", out var modsElement))
        {
            return true;
        }

        if (modsElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in modsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String
                || !WireNames.TryParseModifier(item.GetString()!, out var modifier)
                || (mods & modifier) != 0)
            {
                return false;
            }

            mods |= modifier;
        }

        return true;
    }

    /// <summary>"ms" is required for hold (integer in range) and forbidden for every other phase.</summary>
    private static bool TryParseMs(JsonElement root, KeyPhase phase, out int? ms)
    {
        ms = null;
        var present = root.TryGetProperty("ms", out var msElement);
        if (phase != KeyPhase.Hold)
        {
            return !present;
        }

        if (!present
            || msElement.ValueKind != JsonValueKind.Number
            || !msElement.TryGetInt32(out var value)
            || value is < ProtocolLimits.MinHoldMs or > ProtocolLimits.MaxHoldMs)
        {
            return false;
        }

        ms = value;
        return true;
    }

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetName(JsonElement root, string name, out string value) =>
        TryGetString(root, name, out value) && TextRules.IsValidText(value, 1, ProtocolLimits.MaxNameScalars);

    private static ParseResult Malformed(string detail) =>
        ParseResult.Fail(new ParseFailure(ParseFailureKind.Malformed, detail));

    private static ParseResult Rejected(Guid id, RejectReason reason, string detail) =>
        ParseResult.Fail(new ParseFailure(ParseFailureKind.CommandRejected, detail, id, reason));
}
