namespace Kasi.Core;

/// <summary>A progress sample; the UI batches these on its own 250 ms tick (CONTRIBUTING 3.2).</summary>
public sealed record JobProgressed(JobId Id, long DoneBytes, long? TotalBytes, long BytesPerSecond) : EngineEvent;
