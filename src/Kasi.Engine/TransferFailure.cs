using System.Net;

namespace Kasi.Engine;

internal sealed record TransferFailure(
    TransferErrorKind Kind,
    string Detail,
    HttpStatusCode? HttpStatusCode = null,
    TimeSpan? RetryAfter = null,
    DiskStatus? DiskStatus = null,
    ProbeFailure? ProbeFailure = null);

internal sealed class TransferException(TransferFailure failure) : Exception(failure.Detail)
{
    public TransferFailure Failure { get; } = failure;
}
