using System.Collections.Frozen;

namespace Csmfd.Core.Input;

/// <summary>
/// Allowlist of chat emotes: "/name" or "/name&lt;variant number&gt;" where name is a listed base emote.
/// The list must be kept in sync with the iPad catalog (Emote.swift).
/// </summary>
public sealed class EmoteAllowlist : IEmoteAllowlist
{
    private const int MaxVariantDigits = 2;

    private readonly FrozenSet<string> _baseNames;

    public EmoteAllowlist(IEnumerable<string> baseNames)
    {
        ArgumentNullException.ThrowIfNull(baseNames);
        var names = baseNames
            .Select(name => name.Trim().TrimStart('/'))
            .Where(name => name.Length > 0)
            .ToList();
        _baseNames = names.Where(IsValidBaseName).ToFrozenSet(StringComparer.Ordinal);
        Skipped = names.Where(name => !IsValidBaseName(name)).ToList();
    }

    public int Count => _baseNames.Count;

    /// <summary>Non-empty entries that are not valid base names and were left out (shown in the server log).</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>One base name per line; empty lines and lines starting with # are ignored.</summary>
    public static EmoteAllowlist Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new EmoteAllowlist(text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')));
    }

    public bool IsAllowed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length < 2 || text[0] != '/')
        {
            return false;
        }

        var body = text.AsSpan(1);
        var digits = 0;
        while (digits < body.Length && char.IsAsciiDigit(body[body.Length - 1 - digits]))
        {
            digits++;
        }

        if (digits > MaxVariantDigits || digits == body.Length)
        {
            return false;
        }

        // Variant "0" or a leading zero ("/wave01") is not a real variant number.
        if (digits > 0 && body[body.Length - digits] == '0')
        {
            return false;
        }

        var lookup = _baseNames.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.Contains(body[..^digits]);
    }

    /// <summary>
    /// Same shape as the stems in Emote.swift: a lowercase ASCII letter, then lowercase letters or '_'
    /// ("wave", "asl_thanks"). Digits are excluded because trailing digits are the variant number.
    /// </summary>
    private static bool IsValidBaseName(string name) =>
        name.Length > 0
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || c == '_');
}
