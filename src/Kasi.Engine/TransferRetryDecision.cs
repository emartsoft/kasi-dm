using System.Net;

namespace Kasi.Engine;

/// <summary>How a failed transfer should be handled (TRD 6.6, KASI-RES-004).</summary>
public enum TransferRetryClass
{
    Transient,
    Throttled,
    AuthRequired,
    Permanent,
    FileChanged,
    Offline,
    DiskSpace,
    DiskError,
    CertificateError,
    Cancelled,
}

/// <summary>One retry-policy decision: whether to retry, how long to wait, and whether to shrink the host pool.</summary>
public sealed record TransferRetryDecision(bool Retry, TimeSpan Delay, bool ReduceConnections)
{
    public static TransferRetryDecision NoRetry { get; } = new(false, TimeSpan.Zero, false);
}
