namespace Kasi.Core;

/// <summary>
/// A changed remote file forced a restart. <paramref name="DiscardedBytes"/> is the flushed progress that was thrown
/// away; the UI may warn the user, but the engine never offers "keep the mixed bytes" (KASI-RES-003).
/// </summary>
public sealed record JobRestarted(JobId Id, string Reason, long DiscardedBytes) : EngineEvent;
