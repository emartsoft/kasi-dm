namespace Kasi.Engine.Tests;

/// <summary>
/// Tests that measure a transfer rate over ten-second windows run alone: beside CPU-heavy transfers on a small CI runner their
/// timers and reads are delayed and the measured rate drops by a fifth.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BandwidthRateGroup
{
    public const string Name = "BandwidthRate";
}
