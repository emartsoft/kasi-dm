using System.Text;
using System.Text.Unicode;

namespace Kasi.Engine;

/// <summary>
/// Parses HTTP <c>Content-Disposition</c> headers according to RFC 6266 and RFC 5987 / 8187 (KASI-ENG-003).
/// Bounds input at <see cref="MaxHeaderLength"/> characters and never throws on malformed values.
/// </summary>
public static class ContentDispositionParser
{
    public const int MaxHeaderLength = 16 * 1024;

    /// <summary>
    /// Extracts the raw (unsanitised) filename from a <c>Content-Disposition</c> header value, preferring
    /// <c>filename*</c> over <c>filename</c>. Returns <see langword="false"/> when no usable filename is present.
    /// </summary>
    public static bool TryGetFileName(string? headerValue, out string fileName) =>
        TryGetFileName(headerValue, out fileName, out _);

    /// <summary>
    /// Extracts the raw (unsanitised) filename across multiple <c>Content-Disposition</c> header values, preferring
    /// <c>filename*</c> over <c>filename</c>.
    /// </summary>
    public static bool TryGetFileName(IEnumerable<string>? headerValues, out string fileName) =>
        TryGetFileName(headerValues, out fileName, out _);

    internal static bool TryGetFileName(string? headerValue, out string fileName, out bool isExtended)
    {
        fileName = string.Empty;
        isExtended = false;

        if (!TryGetCandidates(headerValue, out var extended, out var plain))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(extended))
        {
            fileName = extended;
            isExtended = true;
            return true;
        }

        if (!string.IsNullOrEmpty(plain))
        {
            fileName = plain;
            isExtended = false;
            return true;
        }

        return false;
    }

    internal static bool TryGetFileName(IEnumerable<string>? headerValues, out string fileName, out bool isExtended)
    {
        fileName = string.Empty;
        isExtended = false;

        if (!TryGetCandidates(headerValues, out var firstExtended, out var firstPlain))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(firstExtended))
        {
            fileName = firstExtended;
            isExtended = true;
            return true;
        }

        if (!string.IsNullOrEmpty(firstPlain))
        {
            fileName = firstPlain;
            isExtended = false;
            return true;
        }

        return false;
    }

    internal static bool TryGetCandidates(string? headerValue, out string? extendedFileName, out string? plainFileName)
    {
        extendedFileName = null;
        plainFileName = null;

        if (string.IsNullOrWhiteSpace(headerValue) || headerValue.Length > MaxHeaderLength)
        {
            return false;
        }

        return TryScanHeader(headerValue.AsSpan(), out extendedFileName, out plainFileName);
    }

    internal static bool TryGetCandidates(IEnumerable<string>? headerValues, out string? extendedFileName, out string? plainFileName)
    {
        extendedFileName = null;
        plainFileName = null;

        if (headerValues is null)
        {
            return false;
        }

        var totalLength = 0;
        foreach (var header in headerValues)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            totalLength += header.Length;
            if (totalLength > MaxHeaderLength)
            {
                extendedFileName = null;
                plainFileName = null;
                return false;
            }

            if (TryScanHeader(header.AsSpan(), out var extended, out var plain))
            {
                extendedFileName ??= extended;
                plainFileName ??= plain;
            }
        }

        return extendedFileName is not null || plainFileName is not null;
    }

    private static bool TryScanHeader(ReadOnlySpan<char> header, out string? extendedFileName, out string? plainFileName)
    {
        extendedFileName = null;
        plainFileName = null;

        header = header.Trim();
        if (header.IsEmpty)
        {
            return false;
        }

        var firstSegment = ReadNextSegment(ref header);
        if (!IsValidDispositionType(firstSegment.Trim()))
        {
            return false;
        }

        while (!header.IsEmpty)
        {
            var parameter = ReadNextSegment(ref header).Trim();
            if (parameter.IsEmpty)
            {
                continue;
            }

            var eq = parameter.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var name = parameter[..eq].Trim();
            var rawValue = parameter[(eq + 1)..].Trim();

            if (name.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                if (extendedFileName is null && TryParseExtendedValue(rawValue, out var parsedExtended))
                {
                    extendedFileName = parsedExtended;
                }
            }
            else if (name.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                if (plainFileName is null && TryParsePlainValue(rawValue, out var parsedPlain))
                {
                    plainFileName = parsedPlain;
                }
            }
        }

        return extendedFileName is not null || plainFileName is not null;
    }

    private static ReadOnlySpan<char> ReadNextSegment(ref ReadOnlySpan<char> remaining)
    {
        var inQuotes = false;
        for (var i = 0; i < remaining.Length; i++)
        {
            var c = remaining[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < remaining.Length)
                {
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ';')
            {
                var segment = remaining[..i];
                remaining = remaining[(i + 1)..];
                return segment;
            }
        }

        var last = remaining;
        remaining = [];
        return last;
    }

    private static bool IsValidDispositionType(ReadOnlySpan<char> type)
    {
        if (type.IsEmpty)
        {
            return false;
        }

        foreach (var c in type)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '=' or '"' or ';' or '/' or '\\')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParsePlainValue(ReadOnlySpan<char> value, out string result)
    {
        result = string.Empty;
        if (value.IsEmpty)
        {
            return false;
        }

        if (value[0] == '"')
        {
            if (value.Length < 2 || value[^1] != '"')
            {
                return false;
            }

            var inner = value[1..^1];
            Span<char> buffer = inner.Length <= 1024 ? stackalloc char[inner.Length] : new char[inner.Length];
            var written = 0;

            for (var i = 0; i < inner.Length; i++)
            {
                var c = inner[i];
                if (c == '\\' && i + 1 < inner.Length && inner[i + 1] is '"' or '\\')
                {
                    buffer[written++] = inner[++i];
                }
                else if (c == '"')
                {
                    // An unescaped quote inside the value means the quoted-string was malformed.
                    return false;
                }
                else
                {
                    buffer[written++] = c;
                }
            }

            if (written == 0)
            {
                return false;
            }

            result = buffer[..written].ToString();
            return true;
        }

        foreach (var c in value)
        {
            if (c == '"' || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        result = value.ToString();
        return result.Length > 0;
    }

    private static bool TryParseExtendedValue(ReadOnlySpan<char> value, out string result)
    {
        result = string.Empty;
        if (value.IsEmpty || value[0] == '"' || value[^1] == '"')
        {
            return false;
        }

        var firstTick = value.IndexOf('\'');
        if (firstTick <= 0)
        {
            return false;
        }

        var rest = value[(firstTick + 1)..];
        var secondTick = rest.IndexOf('\'');
        if (secondTick < 0)
        {
            return false;
        }

        var charset = value[..firstTick].Trim();
        var isUtf8 = charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase);
        var isLatin1 = !isUtf8 && charset.Equals("ISO-8859-1", StringComparison.OrdinalIgnoreCase);
        if (!isUtf8 && !isLatin1)
        {
            return false;
        }

        var encoded = rest[(secondTick + 1)..];
        if (encoded.IsEmpty)
        {
            return false;
        }

        Span<byte> bytes = encoded.Length <= 1024 ? stackalloc byte[encoded.Length] : new byte[encoded.Length];
        var byteCount = 0;

        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '%')
            {
                if (i + 2 >= encoded.Length
                    || !TryParseHexNibble(encoded[i + 1], out var hi)
                    || !TryParseHexNibble(encoded[i + 2], out var lo))
                {
                    return false;
                }

                bytes[byteCount++] = (byte)((hi << 4) | lo);
                i += 2;
            }
            else if (c is >= '!' and <= '~' && c is not '"' and not ';')
            {
                bytes[byteCount++] = (byte)c;
            }
            else
            {
                return false;
            }
        }

        if (byteCount == 0)
        {
            return false;
        }

        var slice = bytes[..byteCount];
        if (isUtf8)
        {
            if (!Utf8.IsValid(slice))
            {
                return false;
            }

            result = Encoding.UTF8.GetString(slice);
            return result.Length > 0;
        }

        result = Encoding.Latin1.GetString(slice);
        return result.Length > 0;
    }

    internal static bool TryParseHexNibble(char c, out int value)
    {
        if (c is >= '0' and <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c is >= 'A' and <= 'F')
        {
            value = c - 'A' + 10;
            return true;
        }

        if (c is >= 'a' and <= 'f')
        {
            value = c - 'a' + 10;
            return true;
        }

        value = 0;
        return false;
    }
}
