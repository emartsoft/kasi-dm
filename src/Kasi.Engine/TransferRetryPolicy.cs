using System.Net;

namespace Kasi.Engine;

/// <summary>
/// Exponential-backoff policy for a failed probe or lease (TRD 6.6, KASI-RES-004): 1 s doubling to 60 s, full jitter, at most
/// 10 attempts. A server <c>Retry-After</c> raises the delay to at least the stated value, and a throttled decision asks the
/// caller to reduce the host connection count.
/// </summary>
public sealed record TransferRetryPolicy
{
    public int MaxAttempts { get; init; } = 10;

    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Test-only deterministic jitter; production uses <see cref="Random.Shared"/> full jitter.</summary>
    internal Func<TimeSpan, TimeSpan>? Jitter { get; init; }

    public TransferRetryClass Classify(
        TransferErrorKind kind,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        ProbeFailure? probeFailure = null) => kind switch
        {
            TransferErrorKind.TransientNetworkError => TransferRetryClass.Transient,
            TransferErrorKind.Offline => TransferRetryClass.Offline,
            TransferErrorKind.ProtocolError => TransferRetryClass.Transient,
            TransferErrorKind.HttpStatusError => ClassifyStatus(statusCode, retryAfter),
            TransferErrorKind.ProbeFailed => probeFailure?.Kind switch
            {
                ProbeErrorKind.TransientError => TransferRetryClass.Transient,
                ProbeErrorKind.Throttled => TransferRetryClass.Throttled,
                ProbeErrorKind.AuthRequired => TransferRetryClass.AuthRequired,
                _ => TransferRetryClass.Permanent,
            },
            TransferErrorKind.FileChanged => TransferRetryClass.FileChanged,
            TransferErrorKind.DiskNotEnoughSpace => TransferRetryClass.DiskSpace,
            TransferErrorKind.DiskTargetExists => TransferRetryClass.DiskError,
            TransferErrorKind.DiskError => TransferRetryClass.DiskError,
            TransferErrorKind.CertificateError => TransferRetryClass.CertificateError,
            TransferErrorKind.Cancelled => TransferRetryClass.Cancelled,
            _ => TransferRetryClass.Permanent,
        };

    public TransferRetryDecision Decide(
        TransferErrorKind kind,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        ProbeFailure? probeFailure = null,
        int failedAttempts = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failedAttempts);
        var classification = Classify(kind, statusCode, retryAfter, probeFailure);
        if (classification is not (TransferRetryClass.Transient or TransferRetryClass.Throttled)
            || failedAttempts >= MaxAttempts)
        {
            return TransferRetryDecision.NoRetry;
        }

        var delay = DelayFor(failedAttempts);
        if (retryAfter is { } serverDelay)
        {
            var stated = serverDelay < TimeSpan.Zero ? TimeSpan.Zero : serverDelay;
            if (stated > delay)
            {
                delay = stated;
            }
        }

        return new TransferRetryDecision(
            Retry: true,
            Delay: delay,
            ReduceConnections: classification == TransferRetryClass.Throttled);
    }

    public TimeSpan DelayFor(int failedAttempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(failedAttempt);
        var multiplier = Math.Pow(2, Math.Min(failedAttempt - 1, 30));
        var capTicks = Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * multiplier);
        var cap = TimeSpan.FromTicks((long)capTicks);
        var jittered = Jitter is { } jitter ? jitter(cap) : TimeSpan.FromTicks((long)(Random.Shared.NextDouble() * cap.Ticks));
        return jittered < TimeSpan.Zero ? TimeSpan.Zero : jittered > cap ? cap : jittered;
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(BaseDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxDelay, BaseDelay);
    }

    private static TransferRetryClass ClassifyStatus(HttpStatusCode? statusCode, TimeSpan? retryAfter)
    {
        if (statusCode is null)
        {
            return TransferRetryClass.Permanent;
        }

        var code = (int)statusCode.Value;
        return code switch
        {
            429 => TransferRetryClass.Throttled,
            503 => retryAfter is null ? TransferRetryClass.Transient : TransferRetryClass.Throttled,
            500 or 502 or 504 => TransferRetryClass.Transient,
            401 or 403 => TransferRetryClass.AuthRequired,
            404 or 410 => TransferRetryClass.Permanent,
            >= 500 => TransferRetryClass.Transient,
            _ => TransferRetryClass.Permanent,
        };
    }
}
