using System.Net;
using Kasi.Core;

namespace Kasi.Engine;

/// <summary>Applies the browser cookie's host, path and Secure scope to an HTTP request (KASI-ENG-009).</summary>
internal static class JobCookieMatcher
{
    public static bool Matches(JobCookie cookie, Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(cookie);
        ArgumentNullException.ThrowIfNull(requestUri);
        if (!requestUri.IsAbsoluteUri || requestUri.Scheme is not ("http" or "https")
            || cookie.Secure && requestUri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var domain = cookie.Domain;
        if (string.IsNullOrWhiteSpace(domain))
        {
            return false;
        }

        var includeSubdomains = domain[0] == '.';
        if (includeSubdomains)
        {
            domain = domain[1..];
        }

        if (domain.Length == 0)
        {
            return false;
        }

        var host = requestUri.IdnHost;
        var exact = host.Equals(domain, StringComparison.OrdinalIgnoreCase);
        if (!exact && (!includeSubdomains || IPAddress.TryParse(host, out _) || IPAddress.TryParse(domain, out _)
            || !host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var path = cookie.Path;
        if (string.IsNullOrEmpty(path) || path[0] != '/')
        {
            return false;
        }

        var requestPath = requestUri.AbsolutePath;
        return requestPath.Equals(path, StringComparison.Ordinal)
            || (requestPath.StartsWith(path, StringComparison.Ordinal)
                && (path[^1] == '/' || requestPath[path.Length] == '/'));
    }
}
