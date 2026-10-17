namespace Kasi.TestServer.Chaos;

/// <summary>
/// A combination of faults (TPL 3.2). Every property is optional; the default profile injects nothing.
/// Randomised decisions use a generator seeded from <see cref="Seed"/> and the request's index within the profile,
/// so the same sequence of requests replays the same faults.
/// </summary>
public sealed record ChaosProfile
{
    public int Seed { get; init; }

    /// <summary>Abort the connection after exactly this many body bytes.</summary>
    public long? ResetAfterBytes { get; init; }

    /// <summary>Abort the connection with this probability after each completed 64 KiB of body.</summary>
    public double? ResetProbabilityPer64KiB { get; init; }

    /// <summary>Hold the connection open sending nothing for this long, once, after <see cref="StallAfterBytes"/>.</summary>
    public double? StallSeconds { get; init; }

    public long StallAfterBytes { get; init; }

    /// <summary>Limit body throughput; applies to the connection with <see cref="ThrottleConnectionIndex"/>, or to all when null.</summary>
    public long? ThrottleBytesPerSecond { get; init; }

    /// <summary>Zero-based index of the connection in the order the server accepted them.</summary>
    public int? ThrottleConnectionIndex { get; init; }

    /// <summary>Answer with this status (429, 500, 502, 503, 504, …) instead of the real response.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Fraction of requests that receive <see cref="StatusCode"/>; 1 means all.</summary>
    public double StatusRate { get; init; } = 1;

    /// <summary>Sent as <c>Retry-After</c> with the injected status.</summary>
    public int? RetryAfterSeconds { get; init; }

    /// <summary>Answer 503 on connections beyond the N-th simultaneously open one from the same client address.</summary>
    public int? MaxConnectionsPerClient { get; init; }

    /// <summary>From the N-th request of this profile on, <c>/files</c> serves different bytes with a different ETag and Last-Modified.</summary>
    public int? ChangeContentAfterRequests { get; init; }

    /// <summary>Stop sending after this many body bytes but finish the response, so fewer bytes arrive than declared.</summary>
    public long? TruncateAfterBytes { get; init; }

    public int LatencyMs { get; init; }

    public int LatencyJitterMs { get; init; }
}
