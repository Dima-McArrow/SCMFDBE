namespace Csmfd.Protocol;

/// <summary>Observed notification facts only; membership and confidence belong to the iPad.</summary>
public static class PartyActivity
{
    public const int NameScalars = 96;

    public static bool IsKind(string kind) => kind is
        "partyCreated" or "partyJoined" or "partyMemberJoined" or "partyMemberLeft" or
        "partyLeaderChanged" or "partyDisbanded";

    public static bool HasName(string kind) => kind is
        "partyMemberJoined" or "partyMemberLeft" or "partyLeaderChanged";

    public static bool IsValidText(string kind, string text) => IsKind(kind) &&
        (HasName(kind)
            ? text == text.Trim() && !text.Contains('\uFFFD', StringComparison.Ordinal)
                && TextRules.IsValidText(text, 1, NameScalars)
            : text.Length == 0);
}
