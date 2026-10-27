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
        _baseNames = baseNames
            .Select(name => name.Trim().TrimStart('/'))
            .Where(name => name.Length > 0 && name.All(char.IsAsciiLetterLower))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    public int Count => _baseNames.Count;

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
}
