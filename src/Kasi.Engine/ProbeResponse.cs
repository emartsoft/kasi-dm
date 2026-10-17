using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// Successful probe result containing the resolved metadata and the open response stream for segment 0 (TRD 6.1,
/// KASI-ENG-003, KASI-ENG-007, KASI-RES-005).
/// <para>
/// Ownership: whoever receives a <see cref="ProbeResponse"/> owns it and must dispose it on every code path;
/// disposing it disposes both <see cref="Body"/> and the underlying HTTP response message.
/// </para>
/// </summary>
public sealed class ProbeResponse : ProbeOutcome, IAsyncDisposable, IDisposable
{
    private readonly IDisposable? _responseOwner;
    private int _disposed;

    public ProbeResponse(
        Uri finalUrl,
        string fileName,
        Stream body,
        long? totalBytes = null,
        long? bodyLength = null,
        bool rangesSupported = false,
        string? eTag = null,
        string? lastModified = null,
        string? acceptRanges = null,
        string? contentType = null,
        string? contentEncoding = null,
        IDisposable? responseOwner = null)
    {
        ArgumentNullException.ThrowIfNull(finalUrl);
        if (!finalUrl.IsAbsoluteUri
            || (!string.Equals(finalUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(finalUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("FinalUrl must be an absolute HTTP or HTTPS URI.", nameof(finalUrl));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(body);

        if (totalBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes), totalBytes, "TotalBytes cannot be negative.");
        }

        if (bodyLength is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bodyLength), bodyLength, "BodyLength cannot be negative.");
        }

        var normalisedEncoding = NormaliseContentEncoding(contentEncoding);
        var normalisedETag = NormaliseStrongETag(eTag);
        var normalisedLastModified = string.IsNullOrWhiteSpace(lastModified) ? null : lastModified;
        var effectiveRangesSupported = rangesSupported && normalisedEncoding is null;

        FinalUrl = finalUrl;
        FileName = fileName;
        Body = body;
        TotalBytes = totalBytes;
        BodyLength = bodyLength;
        ContentEncoding = normalisedEncoding;
        RangesSupported = effectiveRangesSupported;
        ETag = normalisedETag;
        LastModified = normalisedLastModified;
        Resumable = effectiveRangesSupported && (normalisedETag is not null || normalisedLastModified is not null);
        AcceptRanges = string.IsNullOrWhiteSpace(acceptRanges) ? null : acceptRanges;
        ContentType = string.IsNullOrWhiteSpace(contentType) ? null : contentType;
        _responseOwner = responseOwner;
    }

    public Uri FinalUrl { get; }

    public string FileName { get; }

    public Stream Body { get; }

    public long? TotalBytes { get; }

    public long? BodyLength { get; }

    public bool RangesSupported { get; }

    public bool Resumable { get; }

    public string? ETag { get; }

    public string? LastModified { get; }

    public string? AcceptRanges { get; }

    public string? ContentType { get; }

    public string? ContentEncoding { get; }

    /// <summary>
    /// Returns a copy of <paramref name="job"/> updated with the probed URL, filename, total size, validators and
    /// resumability flag.
    /// </summary>
    public Job ApplyTo(Job job, bool preserveExistingFileName = false)
    {
        ArgumentNullException.ThrowIfNull(job);

        return job with
        {
            FinalUrl = FinalUrl.AbsoluteUri,
            FileName = preserveExistingFileName && !string.IsNullOrWhiteSpace(job.FileName) ? job.FileName : FileName,
            TotalBytes = TotalBytes,
            ETag = ETag,
            LastModified = LastModified,
            Resumable = Resumable,
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Body.Dispose();
        _responseOwner?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await Body.DisposeAsync().ConfigureAwait(false);
        _responseOwner?.Dispose();
    }

    private static string? NormaliseStrongETag(string? eTag)
    {
        if (string.IsNullOrWhiteSpace(eTag))
        {
            return null;
        }

        var trimmed = eTag.Trim();
        return trimmed.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static string? NormaliseContentEncoding(string? contentEncoding)
    {
        if (string.IsNullOrWhiteSpace(contentEncoding))
        {
            return null;
        }

        var trimmed = contentEncoding.Trim();
        return trimmed.Equals("identity", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }
}
