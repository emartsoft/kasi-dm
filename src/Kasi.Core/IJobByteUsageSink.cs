namespace Kasi.Core;

/// <summary>
/// The narrow seam that reports new bytes written by a transfer to quota accounting. The engine calls it with deltas, never absolute
/// progress, so a resumed job does not count its saved bytes twice (KASI-QUE-007).
/// </summary>
public interface IJobByteUsageSink
{
    void Report(JobId jobId, long bytes);
}
