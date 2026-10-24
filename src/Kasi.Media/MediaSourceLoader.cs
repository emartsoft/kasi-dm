using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kasi.Core;

namespace Kasi.Media;

/// <summary>
/// The single media entry point (M3.2): fetches a manifest (and HLS media playlists) with the page's cookies/referrer,
/// parses it, runs the protection gate, and returns either a <see cref="Playable"/> plan or a <see cref="ProtectedResult"/> refusal.
/// The <see cref="Playable"/> constructor is internal and reachable only from this loader in normal use, so no outside caller can
/// produce a download plan that skipped the gate (TRD 7.3, KASI-MED-005).
/// </summary>
public sealed class MediaSourceLoader : IDisposable
{
    public const int MaxRedirects = 10;
    public const int MaxHlsPlaylists = 256;
    public const string ProtectedMessage = "This video is protected and can't be saved";

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly TimeSpan _timeout;

    public MediaSourceLoader(HttpClient? client = null, TimeSpan? timeout = null)
    {
        _ownsClient = client is null;
#pragma warning disable CA2000 // the HttpClient owns and disposes the handler
        _client = client ?? new HttpClient(CreateHandler());
#pragma warning restore CA2000
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_timeout, TimeSpan.Zero);
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    public async Task<Result> LoadAsync(
        MediaKind kind,
        Uri manifestUri,
        MediaRequestContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestUri);
        if (!manifestUri.IsAbsoluteUri || manifestUri.Scheme is not ("http" or "https"))
        {
            throw new MediaLoadException(MediaLoadError.InvalidUri, "The manifest must be an absolute HTTP or HTTPS URI.");
        }

        context = (context ?? MediaRequestContext.Empty).Validate();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            return kind switch
            {
                MediaKind.Hls => await LoadHlsAsync(manifestUri, context, timeout.Token).ConfigureAwait(false),
                MediaKind.Dash => LoadDash(await FetchTextAsync(manifestUri, context, timeout.Token).ConfigureAwait(false)),
                _ => throw new MediaLoadException(MediaLoadError.UnsupportedKind, "Unsupported media kind."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MediaLoadException(MediaLoadError.Timeout, "The manifest fetch timed out.");
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private async Task<Result> LoadHlsAsync(Uri manifestUri, MediaRequestContext context, CancellationToken cancellationToken)
    {
        var masterFetch = await FetchTextAsync(manifestUri, context, cancellationToken).ConfigureAwait(false);
        manifestUri = masterFetch.Uri;
        var masterText = masterFetch.Text;
        HlsMasterPlaylist master;
        try
        {
            master = HlsParser.ParseMaster(masterText, manifestUri);
        }
        catch (MediaParseException parse) when (parse.Error == MediaParseError.InvalidPlaylist)
        {
            // A media playlist URL is also a valid HLS capture with one implicit quality.
            var media = HlsParser.ParseMedia(masterText, manifestUri);
            if (IsProtected(AllKeys(media), []))
            {
                return new ProtectedResult("hls-drm");
            }

            return new Playable(DirectPresentation(media));
        }

        var playlistUris = master.Variants.Select(variant => variant.PlaylistUri)
            .Concat(master.Renditions.Select(rendition => rendition.PlaylistUri))
            .Distinct()
            .ToArray();
        if (playlistUris.Length > MaxHlsPlaylists)
        {
            throw new MediaLoadException(MediaLoadError.TooManyManifests, "The master playlist has too many media playlists.");
        }

        var playlists = new Dictionary<string, HlsMediaPlaylist>(StringComparer.Ordinal);
        foreach (var uri in playlistUris)
        {
            var fetched = await FetchTextAsync(uri, context, cancellationToken).ConfigureAwait(false);
            playlists[uri.AbsoluteUri] = HlsParser.ParseMedia(fetched.Text, fetched.Uri);
        }

        if (IsProtected(
            master.SessionKeys.Concat(playlists.Values.SelectMany(AllKeys)),
            master.SessionKeys))
        {
            return new ProtectedResult("hls-drm");
        }

        var presentation = HlsPresentationBuilder.Build(master, uri => playlists[uri.AbsoluteUri]);
        return new Playable(presentation);
    }

    private static Result LoadDash(FetchedText fetched)
    {
        var presentation = DashParser.Parse(fetched.Text, fetched.Uri);
        if (presentation.Streams.Any(stream => stream.ProtectionSignals.Any()))
        {
            return new ProtectedResult("dash-drm");
        }

        return new Playable(presentation);
    }

    private static IEnumerable<MediaKey> AllKeys(HlsMediaPlaylist playlist) =>
        playlist.Segments.Select(segment => segment.Key).Prepend(playlist.Key).OfType<MediaKey>();

    private static bool IsProtected(IEnumerable<MediaKey> keys, IEnumerable<MediaKey> sessionKeys)
    {
        foreach (var key in keys.Concat(sessionKeys).Distinct())
        {
            var method = key.Method.Trim().ToUpperInvariant();
            if (method == "NONE")
            {
                continue;
            }

            if (method != "AES-128")
            {
                return true;
            }

            if (key.KeyFormat is { } format && !string.Equals(format, "identity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (key.Uri is not { IsAbsoluteUri: true, Scheme: "https" })
            {
                return true;
            }
        }

        return false;
    }

    private static MediaPresentation DirectPresentation(HlsMediaPlaylist media)
    {
        var track = new MediaTrack(
            $"hls:{media.PlaylistUri.AbsoluteUri}",
            MediaRole.Video,
            null,
            null,
            null,
            null,
            null,
            null,
            media.DurationSeconds,
            media.IsLive,
            media.Initialization,
            media.Segments,
            media.Key);
        return new MediaPresentation(null, media.DurationSeconds, media.IsLive, [new MediaVariant(track, null, null)], [], []);
    }

    private async Task<FetchedText> FetchTextAsync(Uri manifestUri, MediaRequestContext context, CancellationToken cancellationToken)
    {
        var current = manifestUri;
        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
        {
            using var request = BuildRequest(current, manifestUri, context);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                if (location is null
                    || !Uri.TryCreate(current, location, out var next)
                    || !next.IsAbsoluteUri
                    || next.Scheme is not ("http" or "https"))
                {
                    throw new MediaLoadException(MediaLoadError.HttpError, "The manifest redirect has no valid HTTP(S) location.", response.StatusCode);
                }

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MediaLoadException(MediaLoadError.HttpError, "The manifest fetch returned an unsuccessful status.", response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > MediaLimits.MaxManifestBytes)
            {
                throw new MediaLoadException(MediaLoadError.TooLarge, "The manifest exceeds the 10 MB cap.");
            }

#pragma warning disable CA2007 // await using has no ConfigureAwait on its disposal
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore CA2007
            var buffer = new byte[64 * 1024];
            using var text = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (text.Length + read > MediaLimits.MaxManifestBytes)
                {
                    throw new MediaLoadException(MediaLoadError.TooLarge, "The manifest exceeds the 10 MB cap.");
                }

                await text.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return new FetchedText(current, Encoding.UTF8.GetString(text.GetBuffer(), 0, checked((int)text.Length)));
        }

        throw new MediaLoadException(MediaLoadError.HttpError, "The manifest had too many redirects.");
    }

    private static HttpRequestMessage BuildRequest(Uri uri, Uri initialUri, MediaRequestContext context)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(context.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", context.UserAgent);
        }

        if (context.Referrer is { } referrer && IsSameOrigin(referrer, initialUri))
        {
            request.Headers.Referrer = referrer;
        }

        if (context.Origin is { } origin && IsSameOrigin(uri, initialUri))
        {
            request.Headers.TryAddWithoutValidation("Origin", origin.GetLeftPart(UriPartial.Authority));
        }

        var cookies = (context.Cookies ?? [])
            .Where(cookie => MediaCookieMatcher.Matches(cookie, uri))
            .Select(cookie => $"{cookie.Name}={cookie.Value}")
            .ToArray();
        if (cookies.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies));
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return request;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Redirect
        or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static bool IsSameOrigin(Uri first, Uri second) =>
        first.IsAbsoluteUri && second.IsAbsoluteUri
        && Uri.Compare(first, second, UriComponents.SchemeAndServer, UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    public abstract record Result;

    /// <summary>A presentation that passed the protection gate. The internal constructor is reachable only from this assembly.</summary>
    public sealed record Playable : Result
    {
        internal Playable(MediaPresentation presentation)
        {
            Presentation = presentation;
        }

        public MediaPresentation Presentation { get; }
    }

    /// <summary>A refusal. It carries no URL, key URI or segment data.</summary>
    public sealed record ProtectedResult : Result
    {
        internal ProtectedResult(string reasonCode)
        {
            ReasonCode = reasonCode;
        }

        public string ReasonCode { get; }

        public string Message => ProtectedMessage;
    }

    private sealed record FetchedText(Uri Uri, string Text);

    private static class MediaCookieMatcher
    {
        public static bool Matches(JobCookie cookie, Uri requestUri)
        {
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
}
