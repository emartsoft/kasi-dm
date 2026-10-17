namespace Kasi.Core.Logging;

/// <summary>
/// Makes values safe to log (CLAUDE.md rule 3, KASI-NFR-013). Log a redacted form of every URL and path; for cookies,
/// headers, tokens and other secrets log <see cref="Placeholder"/> instead of the value. None of these methods throws.
/// </summary>
public static class Redact
{
    public const string Placeholder = "[redacted]";

    /// <summary>
    /// Scheme, host, port and path only: user info, query string and fragment are dropped. Schemes without a host
    /// (<c>magnet:</c>, <c>data:</c>, <c>file:</c>) keep only the scheme, and text that is not an absolute URL becomes the placeholder.
    /// </summary>
    public static string Url(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Placeholder;
        }

        if (uri.HostNameType == UriHostNameType.Unknown || string.IsNullOrEmpty(uri.Host))
        {
            return $"{uri.Scheme}:{Placeholder}";
        }

        // Cut at the first ? or # ourselves: .NET treats them as part of the path for some schemes (ftp), which would keep the query.
        var cut = url.AsSpan().IndexOfAny('?', '#');
        if (!Uri.TryCreate(cut >= 0 ? url[..cut] : url, UriKind.Absolute, out var withoutQuery))
        {
            return Placeholder;
        }

        var port = withoutQuery.IsDefaultPort ? string.Empty : $":{withoutQuery.Port}";
        return $"{withoutQuery.Scheme}://{withoutQuery.Host}{port}{withoutQuery.AbsolutePath}";
    }

    /// <summary>
    /// The last path segment only (either separator), so the folder and the user name in it never reach a log.
    /// A path that ends at a user's profile folder (<c>...\Users\name</c>) becomes the placeholder.
    /// </summary>
    public static string Path(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return Placeholder;
        }

        var last = segments[^1];
        var parentIsUsers = segments.Length >= 2 && segments[^2].Equals("Users", StringComparison.OrdinalIgnoreCase);
        return parentIsUsers || last.EndsWith(':') ? Placeholder : last;
    }
}
