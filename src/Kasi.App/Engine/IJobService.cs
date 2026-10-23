using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.Engine;

/// <summary>What the Add dialog (and later the picker) asks for. A folder of null means the category's folder under Downloads.</summary>
internal sealed record NewDownload(
    string Url,
    string FileName,
    int QueueId,
    int? CategoryId = null,
    string? Folder = null,
    string? Referrer = null,
    int? Connections = null,
    bool StartNow = true,
    string? ChecksumAlgorithm = null,
    string? Checksum = null,
    IReadOnlyList<JobCookie>? Cookies = null,
    string? UserAgent = null)
{
    public override string ToString() => nameof(NewDownload); // the URL and cookies are secrets (CLAUDE.md rule 3)
}

/// <summary>The outcome of an add. <see cref="Job"/> is null when a duplicate needs a decision first (nothing was added).</summary>
internal sealed record AddResult(DuplicateCheckResult Check, Job? Job);

/// <summary>
/// The UI's one way into the engine and the stores (TRD 5.2 facade). Dialogs and the job list depend on this, not on the engine, so
/// they are tested against a fake; <see cref="JobService"/> is the real one.
/// </summary>
internal interface IJobService
{
    /// <summary>Raised on the engine's dispatcher thread after a change is committed; never post to a control from here.</summary>
    event Action<EngineEvent>? EventRaised;

    Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds (and, when asked, starts) a job. Without a resolution a duplicate is reported and nothing is added.</summary>
    Task<AddResult> AddAsync(NewDownload request, DuplicateAction? resolution = null, CancellationToken cancellationToken = default);

    Task PauseAsync(JobId id, CancellationToken cancellationToken = default);

    Task ResumeAsync(JobId id, CancellationToken cancellationToken = default);

    Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default);
}
