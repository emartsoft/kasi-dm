using System.Net;

namespace Kasi.Engine;

/// <summary>
/// A probe that did not produce a downloadable stream, classified by <see cref="ProbeErrorKind"/> (TRD 6.1, TRD 6.6).
/// Any URL in <see cref="Message"/> must be redacted via <see cref="Core.Logging.Redact.Url"/> (KASI-NFR-013).
/// </summary>
public sealed class ProbeFailure : ProbeOutcome
{
    public ProbeFailure(
        ProbeErrorKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        TimeSpan? retryAfter = null,
        IReadOnlyList<string>? authChallenges = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (retryAfter is { Ticks: < 0 })
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "RetryAfter cannot be negative.");
        }

        Kind = kind;
        Message = message;
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        AuthChallenges = authChallenges is { Count: > 0 } ? [.. authChallenges] : [];
    }

    public ProbeErrorKind Kind { get; }

    public string Message { get; }

    public HttpStatusCode? StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public IReadOnlyList<string> AuthChallenges { get; }
}
