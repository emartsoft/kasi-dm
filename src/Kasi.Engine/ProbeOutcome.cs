namespace Kasi.Engine;

/// <summary>
/// Outcome of probing a download URL: either a live <see cref="ProbeResponse"/> holding segment 0's stream and
/// metadata, or a <see cref="ProbeFailure"/> describing why the probe could not start (TRD 6.1, CONTRIBUTING 3.1).
/// </summary>
public abstract class ProbeOutcome
{
    private protected ProbeOutcome()
    {
    }
}
