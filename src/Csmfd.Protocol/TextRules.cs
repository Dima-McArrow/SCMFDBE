using System.Buffers;
using System.Globalization;
using System.Text;

namespace Csmfd.Protocol;

/// <summary>Validation of free-text and token fields shared by parser and writer.</summary>
public static class TextRules
{
    public const int TokenByteLength = 32;

    /// <summary>32 bytes in base64url without padding.</summary>
    public const int TokenLength = 43;

    /// <summary>
    /// True when the text is well-formed UTF-16 (no lone surrogates), contains no control (Cc, CR/LF/tab/NUL
    /// included) or format (Cf, e.g. soft hyphen, zero-width space) characters and its length in Unicode
    /// scalar values is in range. The iPad rejects both categories (spec 41.21).
    /// </summary>
    public static bool IsValidText(string value, int minScalars, int maxScalars)
    {
        ArgumentNullException.ThrowIfNull(value);

        var remaining = value.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            if (IsControlOrFormat(rune) || ++count > maxScalars)
            {
                return false;
            }

            remaining = remaining[consumed..];
        }

        return count >= minScalars;
    }

    /// <summary>Control (Cc) or format (Cf) character: never allowed in wire text.</summary>
    public static bool IsControlOrFormat(Rune rune) =>
        Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format;

    public static bool IsTokenFormat(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length != TokenLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isBase64Url = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_';
            if (!isBase64Url)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsPairCodeFormat(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == ProtocolLimits.PairCodeLength && value.All(char.IsAsciiDigit);
    }
}
