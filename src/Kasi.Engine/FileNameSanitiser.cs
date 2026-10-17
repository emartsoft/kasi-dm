namespace Kasi.Engine;

/// <summary>
/// Sanitises untrusted filenames from HTTP headers or URLs so they are valid on Windows, stay inside the target folder
/// and never exceed <see cref="MaxLength"/> characters (KASI-ENG-003, TRD 13.1).
/// </summary>
public static class FileNameSanitiser
{
    public const int MaxLength = 200;
    public const int MaxInputLength = 4 * 1024;
    public const string DefaultFileName = "download";

    private const int MaxPreservedExtensionLength = 32;

    /// <summary>
    /// Strips directory components, control and bidirectional characters, replaces invalid filename characters, maps
    /// Windows reserved device names and caps the result at <see cref="MaxLength"/> characters. Never throws.
    /// </summary>
    public static string Sanitise(string? candidate, string fallback = DefaultFileName) =>
        SanitiseCore(candidate)
        ?? (string.Equals(fallback, DefaultFileName, StringComparison.Ordinal)
            ? DefaultFileName
            : SanitiseCore(fallback) ?? DefaultFileName);

    internal static string? SanitiseCore(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxInputLength)
        {
            return null;
        }

        var span = ExtractLeaf(candidate.AsSpan());
        if (span.IsEmpty)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[span.Length];
        var written = FilterCharacters(span, buffer);
        var cleaned = TrimLeadingWhitespaceAndTrailingDotsAndSpaces(buffer[..written]);
        if (cleaned.IsEmpty)
        {
            return null;
        }

        var result = cleaned.ToString();
        if (IsReservedFileName(result))
        {
            result = "_" + result;
        }

        if (result.Length > MaxLength)
        {
            result = TruncatePreservingExtension(result, MaxLength);
            if (result.Length == 0)
            {
                return null;
            }

            if (IsReservedFileName(result))
            {
                result = TruncatePreservingExtension("_" + result, MaxLength);
            }
        }

        return result.Length == 0 ? null : result;
    }

    private static ReadOnlySpan<char> ExtractLeaf(ReadOnlySpan<char> input)
    {
        var lastSlash = input.LastIndexOfAny('/', '\\');
        if (lastSlash >= 0)
        {
            input = input[(lastSlash + 1)..];
        }
        else if (input.Length >= 2 && char.IsAsciiLetter(input[0]) && input[1] == ':')
        {
            input = input[2..];
        }

        return input is "." or ".." ? [] : input;
    }

    private static int FilterCharacters(ReadOnlySpan<char> source, Span<char> destination)
    {
        var written = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (char.IsControl(c) || IsBidiControl(c))
            {
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    destination[written++] = c;
                    destination[written++] = source[++i];
                }
                else
                {
                    destination[written++] = '_';
                }

                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                destination[written++] = '_';
                continue;
            }

            destination[written++] = IsInvalidFileNameChar(c) ? '_' : c;
        }

        return written;
    }

    private static bool IsBidiControl(char c) =>
        c is (>= '\u202A' and <= '\u202E')
            or (>= '\u2066' and <= '\u2069')
            or '\u200E'
            or '\u200F'
            or '\u061C';

    private static bool IsInvalidFileNameChar(char c) =>
        c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '/' or '\\';

    private static ReadOnlySpan<char> TrimLeadingWhitespaceAndTrailingDotsAndSpaces(ReadOnlySpan<char> span)
    {
        var start = 0;
        while (start < span.Length && char.IsWhiteSpace(span[start]))
        {
            start++;
        }

        var end = span.Length;
        while (end > start && (span[end - 1] == '.' || char.IsWhiteSpace(span[end - 1])))
        {
            end--;
        }

        return span[start..end];
    }

    private static bool IsReservedFileName(ReadOnlySpan<char> fileName)
    {
        var dot = fileName.IndexOf('.');
        var stem = dot >= 0 ? fileName[..dot] : fileName;
        stem = TrimLeadingWhitespaceAndTrailingDotsAndSpaces(stem);
        if (stem.IsEmpty)
        {
            return false;
        }

        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return stem.Length == 4
            && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase))
            && IsReservedDeviceDigit(stem[3]);
    }

    private static bool IsReservedDeviceDigit(char c) =>
        c is (>= '0' and <= '9') or '\u00B9' or '\u00B2' or '\u00B3';

    private static string TruncatePreservingExtension(string name, int maxLength)
    {
        var span = name.AsSpan();
        var lastDot = span.LastIndexOf('.');
        var hasExtension = lastDot > 0 && (span.Length - lastDot) <= MaxPreservedExtensionLength;

        if (hasExtension)
        {
            var extension = span[lastDot..];
            var maxStemLength = maxLength - extension.Length;
            if (maxStemLength > 0)
            {
                var stem = SliceSurrogateSafe(span[..lastDot], maxStemLength);
                stem = TrimLeadingWhitespaceAndTrailingDotsAndSpaces(stem);
                if (!stem.IsEmpty)
                {
                    return string.Concat(stem, extension);
                }
            }
        }

        var truncated = SliceSurrogateSafe(span, maxLength);
        truncated = TrimLeadingWhitespaceAndTrailingDotsAndSpaces(truncated);
        return truncated.ToString();
    }

    private static ReadOnlySpan<char> SliceSurrogateSafe(ReadOnlySpan<char> span, int maxLength)
    {
        if (span.Length <= maxLength)
        {
            return span;
        }

        var cut = maxLength;
        if (cut > 0 && char.IsHighSurrogate(span[cut - 1]))
        {
            cut--;
        }

        return span[..cut];
    }
}
