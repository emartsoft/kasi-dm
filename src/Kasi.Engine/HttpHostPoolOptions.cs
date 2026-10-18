using System.Security.Cryptography.X509Certificates;

namespace Kasi.Engine;

/// <summary>
/// Tuning for one <see cref="HttpHostPool"/> (M1.7a, ADR 0006). The defaults follow TRD 5.3 and 6.2: a dedicated, long-lived
/// pool per host, HTTP/2 multiplexing when the server offers it, and no redirect, cookie or decompression behaviour in the
/// transport layer (the probe and the transfer loop own those decisions).
/// </summary>
public sealed record HttpHostPoolOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan KeepAlivePingDelay { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan KeepAlivePingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Lets one HTTP/2 lane open more than one TCP connection when the server's stream limit is reached.</summary>
    public bool EnableMultipleHttp2Connections { get; init; } = true;

    /// <summary>
    /// Test-only trust anchors. Internal so production code cannot reach it, and never paired with a validation callback:
    /// <see cref="System.Net.Security.SslClientAuthenticationOptions.CertificateChainPolicy"/> still enforces the host name
    /// and validity window in <c>SslStream</c> (KASI-NFR-011, TC-ENG-023).
    /// </summary>
    internal X509Certificate2Collection? CustomTrustRoots { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PooledConnectionIdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PooledConnectionLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepAlivePingDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(KeepAlivePingTimeout, TimeSpan.Zero);
    }
}
