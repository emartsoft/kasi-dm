namespace Kasi.TestServer.Chaos;

/// <summary>One request's view of a profile: its zero-based index within the profile and its seeded random source.</summary>
internal sealed class ChaosSession(ChaosProfile profile, int requestIndex)
{
    public const string ItemKey = "kasi.chaos";

    public ChaosProfile Profile { get; } = profile;

    public int RequestIndex { get; } = requestIndex;

    // Random(int) is specified to be stable across runs and machines, which is what replay by seed needs.
    public Random Rng { get; } = new(unchecked((profile.Seed * 1_000_003) + requestIndex));

    public bool Aborted { get; set; }
}
