using Kasi.Core;

namespace Kasi.Engine;

/// <summary>Outcome of one HTTP transfer: either a verified file or a classified failure (M1.7b).</summary>
public abstract class TransferOutcome
{
    private protected TransferOutcome()
    {
    }
}

/// <summary>A transfer that sealed and committed its file (TRD 6.4, 6.5). The URL and path stay in memory only.</summary>
public sealed class TransferCompleted : TransferOutcome
{
    public required Uri FinalUrl { get; init; }

    public required string FileName { get; init; }

    public required string FinalPath { get; init; }

    public required long TotalBytes { get; init; }

    public bool RangesSupported { get; init; }

    public bool Resumable { get; init; }

    public string? ETag { get; init; }

    public string? LastModified { get; init; }

    public IReadOnlyList<Segment> FinalSegments { get; init; } = [];

    public int StructureVersion { get; init; }

    public long EndgameDuplicateBytes { get; init; }

    /// <summary>Lease retries made during the transfer (failed leases that were retried; probe retries are not counted).</summary>
    public int RetryCount { get; init; }

    /// <summary>The allowed connection count at the end; below the configured count after a throttling server reduced it.</summary>
    public int FinalAllowedConnections { get; init; }
}
