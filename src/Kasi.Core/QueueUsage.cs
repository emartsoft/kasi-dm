namespace Kasi.Core;

/// <summary>One queue's data consumption in a quota period (TRD 10.1, KASI-QUE-007).</summary>
public sealed record QueueUsage
{
    public required int QueueId { get; init; }

    /// <summary>The period's start in UTC; <see cref="QuotaReset.Never"/> uses <see cref="DateTimeOffset.UnixEpoch"/>.</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    public required long Bytes { get; init; }
}
