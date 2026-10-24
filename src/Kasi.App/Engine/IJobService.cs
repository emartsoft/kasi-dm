using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.Engine;

/// <summary>
/// What the Add dialog (and later the picker) asks for. A folder of null means the category's folder under Downloads. A category of null
/// means "by file type": the category rules choose it (KASI-CAT-001, 002), using <paramref name="Mime"/> when the name has no extension.
/// </summary>
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
    string? UserAgent = null,
    string? Mime = null)
{
    public override string ToString() => nameof(NewDownload); // the URL and cookies are secrets (CLAUDE.md rule 3)
}

/// <summary>The outcome of an add. <see cref="Job"/> is null when a duplicate needs a decision first (nothing was added).</summary>
internal sealed record AddResult(DuplicateCheckResult Check, Job? Job);

internal enum RefreshProblem
{
    None,
    /// <summary>The job cannot take a new link now: not paused, not HTTP, or no checkpoint to validate against.</summary>
    NotResumable,
    /// <summary>The new link serves another file; the job stays paused.</summary>
    Rejected,
    Failed,
}

/// <summary>How a link refresh went. <see cref="Problem"/> none means the transfer resumed from its saved bytes.</summary>
internal sealed record LinkRefreshResult(RefreshProblem Problem);

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

    /// <summary>The category rules, for a dialog that shows the category a link would get. None by default (a fake that has no rules).</summary>
    Task<IReadOnlyList<CategoryRule>> CategoryRulesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CategoryRule>>([]);

    /// <summary>Adds (and, when asked, starts) a job. Without a resolution a duplicate is reported and nothing is added.</summary>
    Task<AddResult> AddAsync(NewDownload request, DuplicateAction? resolution = null, CancellationToken cancellationToken = default);

    Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives a paused job a fresh link and the cookies that go with it, and returns once the transfer is running again (or was refused),
    /// not when it ends. The transfer carries on after the caller stops waiting.
    /// </summary>
    Task<LinkRefreshResult> RefreshLinkAsync(JobId id, string url, IReadOnlyList<JobCookie> cookies, CancellationToken cancellationToken = default);

    /// <summary>The cap shared by all transfers in bytes per second; null means unlimited.</summary>
    long? SpeedLimit { get; set; }

    /// <summary>Pauses every transfer that is running or starting (as the user, so nothing resumes it by itself).</summary>
    Task PauseAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Resumes every download the user (or a restart or a lost network) paused. A download paused for want of access needs a fresh link first and is left alone.</summary>
    Task ResumeAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a queued job now.</summary>
    Task StartAsync(JobId id, CancellationToken cancellationToken = default);

    Task PauseAsync(JobId id, CancellationToken cancellationToken = default);

    /// <summary>The per-job speed limit and the open-when-completed flag of the Active Transfer window; a running job changes speed at once.</summary>
    Task SetJobOptionsAsync(JobId id, long? speedLimitBps, bool openWhenCompleted, CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task ResumeAsync(JobId id, CancellationToken cancellationToken = default);

    Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default);

    Task StartQueueAsync(int queueId, CancellationToken cancellationToken = default);

    Task StopQueueAsync(int queueId, CancellationToken cancellationToken = default);

    Task<QueueSchedule?> GetScheduleAsync(int queueId, CancellationToken cancellationToken = default);

    Task SaveScheduleAsync(QueueSchedule schedule, CancellationToken cancellationToken = default);

    Task SaveQueueAsync(Core.Queue queue, CancellationToken cancellationToken = default);
}
