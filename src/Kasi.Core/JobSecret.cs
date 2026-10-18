using Kasi.Core.Logging;

namespace Kasi.Core;

/// <summary>Cookies, captured headers and credentials that belong to one job (KASI-ENG-009, KASI-NFR-013).</summary>
public sealed record JobSecret
{
    public IReadOnlyList<JobCookie> Cookies { get; init; } = [];

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public HttpCredentials? Credentials { get; init; }

    public override string ToString() => Redact.Placeholder;
}
