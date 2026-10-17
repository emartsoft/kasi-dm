using System.Text;
using System.Text.Unicode;

namespace Kasi.Engine;

/// <summary>
/// Resolves a safe local filename for a download from its <c>Content-Disposition</c> header or URL path, falling back
/// to <see cref="FileNameSanitiser.DefaultFileName"/> (KASI-ENG-003, TRD 6.1, TRD 13.1).
/// </summary>
public static class DownloadFileName
{
    public const int MaxUrlPathLength = 4 * 1024;

    /// <summary>
    /// Resolves the filename from <paramref name="contentDisposition"/> when present and valid, otherwise from the path
    /// of <paramref name="url"/>, and sanitises the result. Never throws.
    /// </summary>
    public static string Resolve(Uri? url, string? contentDisposition = null) =>
        TryResolveFromContentDisposition(contentDisposition, out var fromHeader)
            ? fromHeader
            : ResolveFromUri(url);

    /// <summary>
    /// Resolves the filename from multiple <paramref name="contentDispositionHeaders"/> when present and valid,
    /// otherwise from the path of <paramref name="url"/>, and sanitises the result. Never throws.
    /// </summary>
    public static string Resolve(Uri? url, IEnumerable<string>? contentDispositionHeaders) =>
        ContentDispositionParser.TryGetCandidates(contentDispositionHeaders, out var extended, out var plain)
            && TrySanitiseCandidates(extended, plain, out var fromHeader)
            ? fromHeader
            : ResolveFromUri(url);

    /// <summary>
    /// Resolves the filename from <paramref name="contentDisposition"/> when present and valid, otherwise from
    /// <paramref name="url"/>, and sanitises the result. Never throws.
    /// </summary>
    public static string Resolve(string? url, string? contentDisposition = null) =>
        TryResolveFromContentDisposition(contentDisposition, out var fromHeader)
            ? fromHeader
            : ResolveFromUrlString(url);

    private static bool TryResolveFromContentDisposition(string? contentDisposition, out string fileName)
    {
        if (ContentDispositionParser.TryGetCandidates(contentDisposition, out var extended, out var plain)
            && TrySanitiseCandidates(extended, plain, out fileName))
        {
            return true;
        }

        fileName = string.Empty;
        return false;
    }

    private static bool TrySanitiseCandidates(string? extended, string? plain, out string fileName)
    {
        if (!string.IsNullOrEmpty(extended))
        {
            var sanitisedExtended = FileNameSanitiser.SanitiseCore(extended);
            if (sanitisedExtended is not null)
            {
                fileName = sanitisedExtended;
                return true;
            }
        }

        if (!string.IsNullOrEmpty(plain))
        {
            var sanitisedPlain = FileNameSanitiser.SanitiseCore(RepairLatin1Utf8(plain));
            if (sanitisedPlain is not null)
            {
                fileName = sanitisedPlain;
                return true;
            }
        }

        fileName = string.Empty;
        return false;
    }

    private static string ResolveFromUri(Uri? url)
    {
        if (url is null)
        {
            return FileNameSanitiser.DefaultFileName;
        }

        if (!url.IsAbsoluteUri)
        {
            return ResolveFromPathSpan(StripQueryAndFragment(url.OriginalString.AsSpan()));
        }

        if (url.HostNameType == UriHostNameType.Unknown || string.IsNullOrEmpty(url.Host))
        {
            return FileNameSanitiser.DefaultFileName;
        }

        // Cut at ? or # on OriginalString first: .NET's ftp scheme treats ? as part of the path and escapes it to %3F in AbsolutePath.
        var withoutQuerySpan = StripQueryAndFragment(url.OriginalString.AsSpan());
        if (withoutQuerySpan.Length > MaxUrlPathLength)
        {
            return FileNameSanitiser.DefaultFileName;
        }

        if (withoutQuerySpan.Length != url.OriginalString.Length
            && Uri.TryCreate(withoutQuerySpan.ToString(), UriKind.Absolute, out var withoutQuery))
        {
            url = withoutQuery;
        }

        var path = StripQueryAndFragment(url.AbsolutePath.AsSpan());
        return ResolveFromPathSpan(path);
    }

    private static string ResolveFromUrlString(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return FileNameSanitiser.DefaultFileName;
        }

        var withoutQuery = StripQueryAndFragment(url.AsSpan().Trim());
        if (withoutQuery.Length > MaxUrlPathLength)
        {
            return FileNameSanitiser.DefaultFileName;
        }

        if (Uri.TryCreate(withoutQuery.ToString(), UriKind.Absolute, out var uri))
        {
            return ResolveFromUri(uri);
        }

        if (withoutQuery.Contains("://", StringComparison.Ordinal))
        {
            return FileNameSanitiser.DefaultFileName;
        }

        return ResolveFromPathSpan(withoutQuery);
    }

    private static ReadOnlySpan<char> StripQueryAndFragment(ReadOnlySpan<char> input)
    {
        var cut = input.IndexOfAny('?', '#');
        return cut >= 0 ? input[..cut] : input;
    }

    private static string ResolveFromPathSpan(ReadOnlySpan<char> path)
    {
        if (path.IsEmpty || path.Length > MaxUrlPathLength)
        {
            return FileNameSanitiser.DefaultFileName;
        }

        var lastSlash = path.LastIndexOfAny('/', '\\');
        var leaf = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        if (leaf.IsEmpty)
        {
            return FileNameSanitiser.DefaultFileName;
        }

        var decoded = PercentDecodeLenient(leaf);
        return FileNameSanitiser.Sanitise(decoded);
    }

    /// <summary>
    /// Re-decodes a plain <c>filename</c> value as UTF-8 when every code unit is in the Latin-1 range (<c>U+0000</c>–<c>U+00FF</c>),
    /// at least one is non-ASCII, and the bytes are valid UTF-8.
    /// </summary>
    internal static string RepairLatin1Utf8(string value)
    {
        var hasNonAscii = false;
        foreach (var c in value)
        {
            if (c > '\u00FF')
            {
                return value;
            }

            if (c >= '\u0080')
            {
                hasNonAscii = true;
            }
        }

        if (!hasNonAscii)
        {
            return value;
        }

        Span<byte> bytes = value.Length <= 1024 ? stackalloc byte[value.Length] : new byte[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            bytes[i] = (byte)value[i];
        }

        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : value;
    }

    /// <summary>
    /// Decodes valid <c>%HH</c> sequences as UTF-8 while keeping malformed <c>%</c> sequences literal so URL paths never throw.
    /// </summary>
    internal static string PercentDecodeLenient(ReadOnlySpan<char> input)
    {
        if (!input.Contains('%'))
        {
            return input.ToString();
        }

        var builder = new StringBuilder(input.Length);
        Span<byte> byteRun = input.Length <= 1024 ? stackalloc byte[input.Length] : new byte[input.Length];

        var i = 0;
        while (i < input.Length)
        {
            if (input[i] == '%'
                && i + 2 < input.Length
                && ContentDispositionParser.TryParseHexNibble(input[i + 1], out var firstHi)
                && ContentDispositionParser.TryParseHexNibble(input[i + 2], out var firstLo))
            {
                var byteCount = 0;
                byteRun[byteCount++] = (byte)((firstHi << 4) | firstLo);
                i += 3;

                while (i + 2 < input.Length
                    && input[i] == '%'
                    && ContentDispositionParser.TryParseHexNibble(input[i + 1], out var hi)
                    && ContentDispositionParser.TryParseHexNibble(input[i + 2], out var lo))
                {
                    byteRun[byteCount++] = (byte)((hi << 4) | lo);
                    i += 3;
                }

                builder.Append(Encoding.UTF8.GetString(byteRun[..byteCount]));
            }
            else
            {
                builder.Append(input[i]);
                i++;
            }
        }

        return builder.ToString();
    }
}
