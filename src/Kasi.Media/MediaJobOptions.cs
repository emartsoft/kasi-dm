using Kasi.Core;
using Kasi.Engine;

namespace Kasi.Media;

/// <summary>Tuning for one media capture run (M3.3): concurrency, storage and the engine resources the job reuses.</summary>
public sealed record MediaJobOptions
{
    public required JobId JobId { get; init; }

    /// <summary>The manifest URL; segment cookies/referrer are scoped from it.</summary>
    public required Uri ManifestUri { get; init; }

    /// <summary>Defaults to <c>%LOCALAPPDATA%\Kasi\media</c>; tests override it.</summary>
    public string? MediaRoot { get; init; }

    /// <summary>The picker's chosen variant; null selects <see cref="MediaPresentation.SelectDefault"/>.</summary>
    public MediaVariant? SelectedVariant { get; init; }

    public MediaRequestContext RequestContext { get; init; } = MediaRequestContext.Empty;

    /// <summary>Whole segments in flight per stream; the setting allows 1 to 16 (default 6).</summary>
    public int Parallelism { get; init; } = 6;

    /// <summary>How often completed-segment rows are flushed to SQLite (TRD 11.3).</summary>
    public TimeSpan CompletionInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan SegmentTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public BandwidthLimiter? BandwidthLimiter { get; init; }

    public BandwidthLimiter.JobBandwidthLimit? JobBandwidthLimit { get; init; }

    public ProxySettings? Proxy { get; init; }

    public HttpHostPoolOptions? PoolOptions { get; init; }

    public TransferRetryPolicy RetryPolicy { get; init; } = new();

    /// <summary>Called once per completed segment file; the app turns it into job progress and speed.</summary>
    public Action<MediaJobProgress>? Progress { get; init; }

    /// <summary>Receives the job's state changes and progress, so the app's grid, tray and queue runner see a media job like any other.</summary>
    public Action<EngineEvent>? Events { get; init; }

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(ManifestUri);
        if (!ManifestUri.IsAbsoluteUri || ManifestUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The manifest must be an absolute HTTP or HTTPS URI.", nameof(ManifestUri));
        }

        if (Parallelism is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(Parallelism), Parallelism, "Parallelism must be between 1 and 16.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CompletionInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SegmentTimeout, TimeSpan.Zero);
    }
}
