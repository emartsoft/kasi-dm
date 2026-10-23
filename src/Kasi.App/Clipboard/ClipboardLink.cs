namespace Kasi.App.Clipboard;

/// <summary>
/// Decides whether copied text is a download link worth offering (KASI-BRW-007, TC-BRW-008): exactly one http, https, ftp or ftps address
/// on one line whose path ends in an extension from the user's list. Anything else, a sentence that contains a link, a page address, a
/// password, is ignored. Pure, so it is tested without a clipboard.
/// </summary>
internal static class ClipboardLink
{
    public const int MaxLength = 8192;

    /// <summary>The address to offer, or null.</summary>
    public static string? TryParse(string? text, IReadOnlyList<string> extensions)
    {
        if (string.IsNullOrWhiteSpace(text) || extensions.Count == 0)
        {
            return null;
        }

        var candidate = text.Trim();
        if (candidate.Length > MaxLength || candidate.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps") || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath).ToLowerInvariant();
        return extensions.Any(extension => path.EndsWith("." + extension.TrimStart('.').ToLowerInvariant(), StringComparison.Ordinal)) ? candidate : null;
    }
}
