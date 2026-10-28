using System.Globalization;
using System.Text.RegularExpressions;

namespace Csmfd.Core.GameLog;

/// <summary>Verified Game.log events the server acts on. Anything else is ignored.</summary>
public abstract record LogEvent(DateTimeOffset At);

/// <summary>"You have joined channel '&lt;Brand Ship&gt; : &lt;owner&gt;'" for a recognised manufacturer.</summary>
public sealed record ShipChannelJoined(DateTimeOffset At, string ShipName, string Owner, string ManufacturerPrefix) : LogEvent(At);

public sealed record VehicleSpawning(DateTimeOffset At, string EntityId, string Owner) : LogEvent(At);

/// <summary>A navigation line naming a vehicle model and entity id (used only to bind the current ship).</summary>
public sealed record VehicleObserved(DateTimeOffset At, string Model, string EntityId) : LogEvent(At);

public sealed record QuantumTargetSelected(DateTimeOffset At, string Model, string EntityId, string Target) : LogEvent(At);

/// <summary>The local client releases the control token of a vehicle.</summary>
public sealed record LocalClearDriver(DateTimeOffset At, string Model, string EntityId) : LogEvent(At);

public sealed record HangarRequestCompleted(DateTimeOffset At) : LogEvent(At);

/// <summary>Client quit or left the universe (SystemQuit / remote disconnect from SC_Default).</summary>
public sealed record GameSessionEnded(DateTimeOffset At) : LogEvent(At);

/// <summary>
/// Deterministic Game.log line parser (spec 41.14). A cheap ordinal marker check runs first; only
/// matching lines reach a source-generated regex with a timeout. Formats come from real lines of
/// build 12913470; the log is a diagnostic file and may change between game versions.
/// </summary>
public static partial class GameLogParser
{
    private const int RegexTimeoutMs = 50;

    /// <summary>Channel brand → manufacturer prefix used in model names (and by the iPad theme table).</summary>
    private static readonly (string Brand, string Prefix)[] Brands =
    [
        ("Consolidated Outland", "CNOU"),
        ("Crusader", "CRUS"),
        ("Drake", "DRAK"),
        ("Aegis", "AEGS"),
        ("Anvil", "ANVL"),
        ("Origin", "ORIG"),
        ("Mirai", "MRAI"),
        ("Esperia", "ESPR"),
        ("Greycat", "GRIN"),
        ("Tumbril", "TMBL"),
        ("Kruger", "KRIG"),
        ("Aopoa", "XIAN"),
        ("Gatac", "GAMA"),
        ("Vanduul", "VNCL"),
        ("Banu", "BANU"),
        ("Argo", "ARGO"),
        ("MISC", "MISC"),
        ("CNOU", "CNOU"),
        ("RSI", "RSI"),
    ];

    public static LogEvent? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Length < 27 || line[0] != '<')
        {
            return null;
        }

        try
        {
            return ParseCore(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>Manufacturer prefix of a channel ship name, or null when the brand is not recognised.</summary>
    public static string? PrefixForShipName(string shipName)
    {
        ArgumentNullException.ThrowIfNull(shipName);
        foreach (var (brand, prefix) in Brands)
        {
            if (shipName.StartsWith(brand, StringComparison.OrdinalIgnoreCase)
                && (shipName.Length == brand.Length || shipName[brand.Length] == ' '))
            {
                return prefix;
            }
        }

        return null;
    }

    /// <summary>"Drake Cutter Rambler" matches model "DRAK_Cutter_Rambler" (and longer variants of it).</summary>
    public static bool ShipNameMatchesModel(string shipName, string model)
    {
        ArgumentNullException.ThrowIfNull(shipName);
        ArgumentNullException.ThrowIfNull(model);

        // Verified display/model aliases; do not guess by a shared manufacturer or hull word.
        var name = shipName switch
        {
            "RSI Aurora Mk I LN" => "RSI Aurora GS LN",
            "Anvil C8R Pisces Rescue" => "Anvil C8R Pisces",
            _ => shipName,
        };
        foreach (var (brand, prefix) in Brands)
        {
            if (name.StartsWith(brand, StringComparison.OrdinalIgnoreCase))
            {
                name = prefix + name[brand.Length..];
                break;
            }
        }

        var normalizedName = Normalize(name);
        var normalizedModel = Normalize(model);
        return normalizedName.Length >= 6 && normalizedModel.StartsWith(normalizedName, StringComparison.Ordinal);
    }

    private static LogEvent? ParseCore(string line)
    {
        if (line.Contains("<SHUDEvent_OnNotification> Added notification \"", StringComparison.Ordinal))
        {
            return ParseNotification(line);
        }

        if (line.Contains("<Vehicle Control Flow>", StringComparison.Ordinal))
        {
            var match = ClearDriverRegex().Match(line);
            return match.Success && TryTime(line, out var at)
                ? new LocalClearDriver(at, StripId(match.Groups["model"].Value), match.Groups["id"].Value)
                : null;
        }

        if (line.Contains("<CEntityComponentShipListProvider::SetVehicleSpawningInformations>", StringComparison.Ordinal))
        {
            var match = SpawningRegex().Match(line);
            return match.Success && TryTime(line, out var at)
                ? new VehicleSpawning(at, match.Groups["id"].Value, match.Groups["owner"].Value)
                : null;
        }

        if (line.Contains("[Team_CGP4][QuantumTravel]", StringComparison.Ordinal))
        {
            return ParseNavigation(line);
        }

        if (line.Contains("<SystemQuit>", StringComparison.Ordinal))
        {
            return TryTime(line, out var at) ? new GameSessionEnded(at) : null;
        }

        if (line.Contains("<Channel Disconnected>", StringComparison.Ordinal)
            && line.Contains("isRemote=1", StringComparison.Ordinal)
            && line.Contains("gamerules=\"SC_Default\"", StringComparison.Ordinal))
        {
            return TryTime(line, out var at) ? new GameSessionEnded(at) : null;
        }

        return null;
    }

    private static LogEvent? ParseNotification(string line)
    {
        if (!TryTime(line, out var at))
        {
            return null;
        }

        if (line.Contains("Added notification \"Hangar Request Completed:", StringComparison.Ordinal))
        {
            return new HangarRequestCompleted(at);
        }

        var match = ChannelRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var shipName = match.Groups["name"].Value.Trim();
        var prefix = PrefixForShipName(shipName);
        return prefix is null ? null : new ShipChannelJoined(at, shipName, match.Groups["owner"].Value.Trim(), prefix);
    }

    private static LogEvent? ParseNavigation(string line)
    {
        var vehicle = NavigationVehicleRegex().Match(line);
        if (!vehicle.Success || !TryTime(line, out var at))
        {
            return null;
        }

        var model = StripId(vehicle.Groups["model"].Value);
        var entityId = vehicle.Groups["id"].Value;

        if (line.Contains("<Player Selected Quantum Target - Local>", StringComparison.Ordinal))
        {
            var target = QuantumTargetRegex().Match(line);
            return target.Success ? new QuantumTargetSelected(at, model, entityId, target.Groups["target"].Value) : null;
        }

        return new VehicleObserved(at, model, entityId);
    }

    private static bool TryTime(string line, out DateTimeOffset at)
    {
        var end = line.IndexOf('>', StringComparison.Ordinal);
        if (end > 1
            && DateTimeOffset.TryParse(line.AsSpan(1, end - 1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out at))
        {
            return true;
        }

        at = default;
        return false;
    }

    /// <summary>"DRAK_Cutter_Rambler_903627021939" → "DRAK_Cutter_Rambler".</summary>
    private static string StripId(string modelWithId) => TrailingIdRegex().Replace(modelWithId, string.Empty);

    private static string Normalize(string value)
    {
        Span<char> buffer = stackalloc char[Math.Min(value.Length, 256)];
        var length = 0;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) && length < buffer.Length)
            {
                buffer[length++] = char.ToLowerInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    [GeneratedRegex("""Added notification "You have joined channel '(?<name>[^:]+?) : (?<owner>[^']+)'""", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex ChannelRegex();

    [GeneratedRegex(@"CVehicleMovementBase::ClearDriver: Local client node \[\d+\] releasing control token for '(?<model>[A-Za-z0-9_]+)' \[(?<id>\d+)\]", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex ClearDriverRegex();

    [GeneratedRegex(@"VehicleEntityId: \[(?<id>\d+)\], LandingArea: (?<owner>[^']+)'s", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex SpawningRegex();

    [GeneratedRegex(@"\|\s*(?<model>[A-Z][A-Za-z0-9_]+)\[(?<id>\d+)\]\|", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex NavigationVehicleRegex();

    [GeneratedRegex(@"Player has selected point (?<target>[A-Za-z0-9_\-]+) as their destination", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex QuantumTargetRegex();

    [GeneratedRegex(@"_\d+$", RegexOptions.CultureInvariant, RegexTimeoutMs)]
    private static partial Regex TrailingIdRegex();
}
