namespace Kasi.Core;

/// <summary>Raised after the new state has been persisted, so a view never shows a state the database does not hold (TRD 6.9).</summary>
public sealed record JobStateChanged(JobId Id, JobState From, JobState To, string? Error) : EngineEvent;
