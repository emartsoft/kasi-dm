using Kasi.Core.Logging;

namespace Kasi.Core;

/// <summary>Credentials for HTTP Basic or Digest authentication (KASI-ENG-009).</summary>
public sealed record HttpCredentials
{
    public required string UserName { get; init; }

    public required string Password { get; init; }

    public override string ToString() => Redact.Placeholder;
}
