using Kasi.Core;

namespace Kasi.Engine;

/// <summary>Existing job and destination conflicts discovered before a job is added.</summary>
public sealed record DuplicateCheckResult(
    Job? ExistingJob,
    bool TargetFileExistsOnDisk,
    bool PartFileExistsOnDisk,
    string SuggestedFileName,
    IReadOnlyList<DuplicateAction> AvailableActions)
{
    public bool HasConflict => ExistingJob is not null || TargetFileExistsOnDisk || PartFileExistsOnDisk;
}
