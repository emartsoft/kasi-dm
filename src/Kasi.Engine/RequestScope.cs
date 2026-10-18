using Kasi.Core;

namespace Kasi.Engine;

/// <summary>Decides which headers and browser cookies may leave the machine for each probe hop and lease.</summary>
internal sealed class RequestScope
{
    private readonly IReadOnlyDictionary<string, (string Value, Uri? Origin, bool OriginOnly)> _headers;
    private readonly IReadOnlyList<JobCookie> _cookies;

    public RequestScope(Uri? initialUrl, IReadOnlyDictionary<string, string>? requestHeaders, JobSecret? secret)
    {
        var headers = new Dictionary<string, (string Value, Uri? Origin, bool OriginOnly)>(StringComparer.OrdinalIgnoreCase);
        if (requestHeaders is not null)
        {
            foreach (var header in requestHeaders)
            {
                headers[header.Key] = (header.Value, initialUrl, IsSensitiveHeader(header.Key));
            }
        }

        if (secret?.Headers is not null)
        {
            foreach (var header in secret.Headers)
            {
                // a captured header is a secret whatever its name (X-Api-Key, X-Amz-Security-Token), so it never leaves its origin
                headers[header.Key] = (header.Value, secret.SensitiveOrigin ?? initialUrl, true);
            }
        }

        _headers = headers;
        _cookies = secret?.Cookies ?? [];
    }

    public void Apply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = request.RequestUri ?? throw new ArgumentException("The HTTP request needs an absolute URI.", nameof(request));
        string? rawCookieHeader = null;
        foreach (var header in _headers)
        {
            if (header.Value.OriginOnly
                && (header.Value.Origin is null || !IsSameOrigin(header.Value.Origin, url)))
            {
                continue;
            }

            if (header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            {
                rawCookieHeader = header.Value.Value;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value.Value);
            }
        }

        var selectedCookies = _cookies.Where(cookie => JobCookieMatcher.Matches(cookie, url))
            .Select(cookie => $"{cookie.Name}={cookie.Value}");
        var cookieHeader = string.Join("; ", selectedCookies);
        if (!string.IsNullOrEmpty(rawCookieHeader))
        {
            cookieHeader = string.IsNullOrEmpty(cookieHeader) ? rawCookieHeader : rawCookieHeader + "; " + cookieHeader;
        }

        if (!string.IsNullOrEmpty(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }
    }

    public static bool IsSameOrigin(Uri first, Uri second) =>
        first.IsAbsoluteUri && second.IsAbsoluteUri
        && Uri.Compare(first, second, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public static bool IsSensitiveHeader(string name) =>
        name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);
}
