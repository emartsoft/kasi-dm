using System.Net;
using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// A transfer that stopped without committing a file (M1.7b). <see cref="Detail"/> is diagnostic only and already redacted;
/// the snapshot is taken before the final flush so it can be recorded as a checkpoint.
/// </summary>
public sealed class TransferFailed : TransferOutcome
{
    public required TransferErrorKind ErrorKind { get; init; }

    public required string Detail { get; init; }

    public long BytesFlushed { get; init; }

    /// <summary>
    /// Segment cursors taken before the final flush, offered only when that flush succeeded; empty when the flush failed or
    /// when nothing was open yet. A caller records these cursors and nothing else (ADR 0003).
    /// </summary>
    public IReadOnlyList<Segment> SegmentsSnapshot { get; init; } = [];

    public string? ResolvedFileName { get; init; }

    public long? TotalBytes { get; init; }

    public ProbeFailure? ProbeFailure { get; init; }

    public DiskStatus? DiskStatus { get; init; }

    public HttpStatusCode? HttpStatusCode { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }
}
