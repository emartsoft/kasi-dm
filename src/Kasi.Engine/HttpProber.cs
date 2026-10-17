using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using Kasi.Core.Logging;

namespace Kasi.Engine;

/// <summary>
/// Probes an HTTP or HTTPS URL with <c>GET</c> and <c>Range: bytes=0-</c> (no wasted <c>HEAD</c>), following up to
/// <see cref="MaxRedirects"/> hops, re-probing once without <c>Range</c> on <c>416</c>, and keeping the response stream
/// open for segment 0 (TRD 6.1, KASI-ENG-003, KASI-ENG-007, KASI-ENG-010, KASI-RES-005).
/// <para>
/// Preconditions on the supplied <see cref="HttpMessageInvoker"/>: <c>AllowAutoRedirect = false</c> and
/// <c>AutomaticDecompression = DecompressionMethods.None</c>.
/// </para>
/// </summary>
public sealed class HttpProber
{
    public const int MaxRedirects = 10;

    private readonly HttpMessageInvoker _httpClient;
    private readonly TimeProvider _time;

    public HttpProber(HttpMessageInvoker httpClient, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ProbeOutcome> ProbeAsync(
        Uri url,
        long? firstRangeLength = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (firstRangeLength is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstRangeLength),
                firstRangeLength,
                "firstRangeLength must be positive when specified.");
        }

        if (!IsSupportedHttpUri(url))
        {
            return new ProbeFailure(
                ProbeErrorKind.InvalidRedirect,
                $"Unsupported URL '{Redact.Url(url.OriginalString)}': only absolute HTTP and HTTPS URLs can be probed.");
        }

        var currentUrl = url;
        var redirects = 0;
        var includeRange = true;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HttpResponseMessage? response = null;
            try
            {
                using var request = CreateRequest(currentUrl, includeRange, firstRangeLength);
                response = await SendHeadersReadAsync(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (IsRedirect(response.StatusCode))
                {
                    var statusCode = response.StatusCode;
                    var location = response.Headers.Location;
                    response.Dispose();
                    response = null;

                    if (location is null
                        || !Uri.TryCreate(currentUrl, location, out var targetUrl)
                        || !IsSupportedHttpUri(targetUrl))
                    {
                        return new ProbeFailure(
                            ProbeErrorKind.InvalidRedirect,
                            $"Redirect from '{Redact.Url(currentUrl.AbsoluteUri)}' had an invalid or unsupported Location header.",
                            statusCode);
                    }

                    redirects++;
                    if (redirects > MaxRedirects)
                    {
                        return new ProbeFailure(
                            ProbeErrorKind.TooManyRedirects,
                            $"Exceeded the maximum of {MaxRedirects} redirects while probing '{Redact.Url(url.AbsoluteUri)}'.",
                            statusCode);
                    }

                    currentUrl = targetUrl;
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    response.Dispose();
                    response = null;

                    if (includeRange)
                    {
                        includeRange = false;
                        continue;
                    }

                    return new ProbeFailure(
                        ProbeErrorKind.ProtocolError,
                        $"Server returned 416 Range Not Satisfiable for '{Redact.Url(currentUrl.AbsoluteUri)}' without a Range header.",
                        HttpStatusCode.RequestedRangeNotSatisfiable);
                }

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    return await HandlePartialContentAsync(currentUrl, response, cancellationToken).ConfigureAwait(false);
                }

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return await HandleOkContentAsync(currentUrl, response, cancellationToken).ConfigureAwait(false);
                }

                var failure = MapErrorResponse(currentUrl, response);
                response.Dispose();
                response = null;
                return failure;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                response?.Dispose();
                throw;
            }
            catch (OperationCanceledException)
            {
                response?.Dispose();
                return new ProbeFailure(
                    ProbeErrorKind.TransientError,
                    $"Request timed out while probing '{Redact.Url(currentUrl.AbsoluteUri)}'.");
            }
            catch (HttpRequestException ex)
            {
                response?.Dispose();
                var kind = IsCertificateFailure(ex)
                    ? ProbeErrorKind.CertificateError
                    : ProbeErrorKind.TransientError;
                return new ProbeFailure(
                    kind,
                    $"HTTP request failed while probing '{Redact.Url(currentUrl.AbsoluteUri)}': {ex.Message}",
                    ex.StatusCode);
            }
        }
    }

    private static HttpRequestMessage CreateRequest(Uri url, bool includeRange, long? firstRangeLength)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (includeRange)
        {
            request.Headers.Range = firstRangeLength is { } length
                ? new RangeHeaderValue(0, length - 1)
                : new RangeHeaderValue(0, null);
        }

        return request;
    }

    private Task<HttpResponseMessage> SendHeadersReadAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _httpClient is HttpClient client
            ? client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            : _httpClient.SendAsync(request, cancellationToken);

    private static async Task<ProbeOutcome> HandlePartialContentAsync(
        Uri finalUrl,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange is null
            || !string.Equals(contentRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || !contentRange.HasRange
            || contentRange.From != 0
            || contentRange.To is null or < 0
            || (contentRange.HasLength && (contentRange.Length is null or <= 0 || contentRange.To >= contentRange.Length)))
        {
            response.Dispose();
            return new ProbeFailure(
                ProbeErrorKind.ProtocolError,
                $"Server returned 206 Partial Content with an invalid Content-Range header for '{Redact.Url(finalUrl.AbsoluteUri)}'.",
                HttpStatusCode.PartialContent);
        }

        var totalBytes = contentRange.HasLength ? contentRange.Length : null;
        var bodyLength = response.Content.Headers.ContentLength ?? (contentRange.To.Value - contentRange.From.Value + 1);
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return CreateProbeResponse(
            finalUrl,
            response,
            body,
            totalBytes: totalBytes,
            bodyLength: bodyLength,
            rangesSupported: true);
    }

    private static async Task<ProbeOutcome> HandleOkContentAsync(
        Uri finalUrl,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var isChunked = response.Headers.TransferEncodingChunked == true;
        var contentLength = isChunked ? null : response.Content.Headers.ContentLength;
        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return CreateProbeResponse(
            finalUrl,
            response,
            body,
            totalBytes: contentLength,
            bodyLength: contentLength,
            rangesSupported: false);
    }

    private static ProbeResponse CreateProbeResponse(
        Uri finalUrl,
        HttpResponseMessage response,
        Stream body,
        long? totalBytes,
        long? bodyLength,
        bool rangesSupported)
    {
        var eTag = response.Headers.ETag is { IsWeak: false } strongTag ? strongTag.Tag : null;
        var lastModified = response.Content.Headers.TryGetValues("Last-Modified", out var lmValues)
            ? lmValues.FirstOrDefault()
            : null;
        var acceptRanges = response.Headers.TryGetValues("Accept-Ranges", out var arValues)
            ? string.Join(", ", arValues)
            : null;
        var contentType = response.Content.Headers.ContentType?.MediaType;
        var contentEncoding = response.Content.Headers.ContentEncoding
            .FirstOrDefault(e => !string.Equals(e, "identity", StringComparison.OrdinalIgnoreCase));
        var fileName = response.Content.Headers.TryGetValues("Content-Disposition", out var cdValues)
            ? DownloadFileName.Resolve(finalUrl, cdValues)
            : DownloadFileName.Resolve(finalUrl);

        return new ProbeResponse(
            finalUrl: finalUrl,
            fileName: fileName,
            body: body,
            totalBytes: totalBytes,
            bodyLength: bodyLength,
            rangesSupported: rangesSupported,
            eTag: eTag,
            lastModified: lastModified,
            acceptRanges: acceptRanges,
            contentType: contentType,
            contentEncoding: contentEncoding,
            responseOwner: response);
    }

    private ProbeFailure MapErrorResponse(Uri currentUrl, HttpResponseMessage response)
    {
        var status = response.StatusCode;
        var redacted = Redact.Url(currentUrl.AbsoluteUri);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.ProxyAuthenticationRequired)
        {
            var challenges = status == HttpStatusCode.ProxyAuthenticationRequired
                ? response.Headers.ProxyAuthenticate
                : response.Headers.WwwAuthenticate;
            var schemes = challenges
                .Select(c => c.Scheme)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ProbeFailure(
                ProbeErrorKind.AuthRequired,
                $"Server returned {(int)status} ({status}) for '{redacted}'.",
                status,
                authChallenges: schemes);
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = TryGetRetryAfter(response.Headers.RetryAfter);
            return new ProbeFailure(
                ProbeErrorKind.Throttled,
                $"Server throttled request with 429 Too Many Requests for '{redacted}'.",
                status,
                retryAfter: retryAfter);
        }

        if (status == HttpStatusCode.ServiceUnavailable)
        {
            var retryAfter = TryGetRetryAfter(response.Headers.RetryAfter);
            if (retryAfter is not null)
            {
                return new ProbeFailure(
                    ProbeErrorKind.Throttled,
                    $"Server returned 503 Service Unavailable with Retry-After for '{redacted}'.",
                    status,
                    retryAfter: retryAfter);
            }

            return new ProbeFailure(
                ProbeErrorKind.TransientError,
                $"Server returned 503 Service Unavailable for '{redacted}'.",
                status);
        }

        if (status == HttpStatusCode.RequestTimeout || (int)status is >= 500 and <= 599)
        {
            return new ProbeFailure(
                ProbeErrorKind.TransientError,
                $"Server returned transient status {(int)status} ({status}) for '{redacted}'.",
                status);
        }

        if ((int)status is >= 400 and <= 499)
        {
            return new ProbeFailure(
                ProbeErrorKind.PermanentError,
                $"Server returned permanent status {(int)status} ({status}) for '{redacted}'.",
                status);
        }

        return new ProbeFailure(
            ProbeErrorKind.ProtocolError,
            $"Unexpected HTTP status {(int)status} ({status}) while probing '{redacted}'.",
            status);
    }

    private TimeSpan? TryGetRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header.Date is { } date)
        {
            var remaining = date - _time.GetUtcNow();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        return null;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsSupportedHttpUri(Uri uri) =>
        uri.IsAbsoluteUri
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsCertificateFailure(HttpRequestException exception)
    {
        if (exception.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return true;
        }

        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }
}
