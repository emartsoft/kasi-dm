using Kasi.Core.Logging;

namespace Kasi.Core;

/// <summary>A browser cookie saved with a job. Matching its host, path and secure flag is handled by the engine (KASI-ENG-009).</summary>
public sealed record JobCookie
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public required string Domain { get; init; }

    public string Path { get; init; } = "/";

    public bool Secure { get; init; }

    public bool HttpOnly { get; init; }

    public override string ToString() => Redact.Placeholder;
}
