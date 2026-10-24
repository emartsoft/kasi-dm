using Kasi.Core;

namespace Kasi.Media;

/// <summary>
/// The page context the browser supplied for a capture: cookies, referrer, user agent and origin. Only the manifest and its
/// HLS media playlists are fetched with these values; no segment is requested by the loader.
/// </summary>
public sealed record MediaRequestContext(
    IReadOnlyList<JobCookie>? Cookies = null,
    Uri? Referrer = null,
    string? UserAgent = null,
    Uri? Origin = null)
{
    public static MediaRequestContext Empty { get; } = new();

    internal MediaRequestContext Validate()
    {
        if (Referrer is { } referrer && (!referrer.IsAbsoluteUri || referrer.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("The referrer must be an absolute HTTP or HTTPS URI.", nameof(Referrer));
        }

        if (Origin is { } origin && (!origin.IsAbsoluteUri || origin.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("The origin must be an absolute HTTP or HTTPS URI.", nameof(Origin));
        }

        return this;
    }
}
