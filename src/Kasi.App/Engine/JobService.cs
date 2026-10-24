using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.Engine;

/// <summary><see cref="IJobService"/> over the engine and the repositories.</summary>
internal sealed class JobService(
    DownloadEngine engine,
    IJobRepository jobs,
    ICategoryRepository categories,
    IQueueRepository queues,
    string downloadsFolder,
    IJobSecretRepository? secrets = null,
    BandwidthLimiter? bandwidth = null,
    IQueueRunner? queueRunner = null,
    IQueueScheduleRepository? schedules = null,
    TimeProvider? timeProvider = null,
    ICategoryRuleRepository? categoryRules = null) : IJobService
{
    private static readonly TimeSpan _resumeWait = TimeSpan.FromSeconds(20);
    private readonly bool _wired = WireRunner(engine, queueRunner);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public event Action<EngineEvent>? EventRaised
    {
        add => engine.EventRaised += value;
        remove => engine.EventRaised -= value;
    }

    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) => jobs.ListAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default) => categories.ListAsync(cancellationToken);

    public Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default) => queues.ListAsync(cancellationToken);

    public Task<IReadOnlyList<CategoryRule>> CategoryRulesAsync(CancellationToken cancellationToken = default) =>
        categoryRules is null ? Task.FromResult<IReadOnlyList<CategoryRule>>([]) : categoryRules.ListAsync(cancellationToken);

    public async Task<AddResult> AddAsync(NewDownload request, DuplicateAction? resolution = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
        {
            throw new ArgumentException("Only http, https, ftp and ftps links can be added.", nameof(request));
        }

        string? checksumAlgo = null;
        string? checksum = null;
        if (request.Checksum is not null || request.ChecksumAlgorithm is not null)
        {
            if (!ExpectedChecksum.TryParse(request.ChecksumAlgorithm, request.Checksum, out var expected) || expected is null)
            {
                throw new ArgumentException("The checksum is not a valid hash for its algorithm.", nameof(request));
            }

            checksumAlgo = request.ChecksumAlgorithm!.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);
            checksum = expected.Hex;
        }

        // no category named: the rules choose it from the link, the file name and the MIME type (a person's choice is never overridden)
        var (categoryId, onlyFallback) = request.CategoryId is { } named
            ? (named, false)
            : await ResolveCategoryAsync(uri, request, cancellationToken).ConfigureAwait(false);
        var folder = request.Folder ?? await FolderForAsync(categoryId, cancellationToken).ConfigureAwait(false);
        var last = (await jobs.ListAsync(request.QueueId, cancellationToken: cancellationToken).ConfigureAwait(false)).Select(j => j.QueuePos).DefaultIfEmpty(0).Max();
        var job = new Job
        {
            Id = JobId.New(_time),
            Kind = uri.Scheme.StartsWith("ftp", StringComparison.Ordinal) ? JobKind.Ftp : JobKind.Http,
            Url = request.Url,
            Referrer = request.Referrer,
            FileName = request.FileName,
            Folder = folder,
            QueueId = request.QueueId,
            QueuePos = last + 1,
            CategoryId = categoryId,
            CategoryAuto = onlyFallback && request.Folder is null, // nothing decided it: the probe's MIME type may (CategoryRouter)
            Connections = request.Connections,
            ChecksumAlgo = checksumAlgo,
            Checksum = checksum,
            CreatedUtc = _time.GetUtcNow(),
        };

        var check = await engine.AddJobAsync(job, resolution, cancellationToken).ConfigureAwait(false);
        if (check.HasConflict && resolution is null)
        {
            return new AddResult(check, null);
        }

        if (check.ExistingJob is null && resolution != DuplicateAction.Resume && secrets is not null && HasSecret(request))
        {
            // the job row exists now, which the secret's foreign key needs; nothing starts before this is stored
            await secrets.SetAsync(job.Id, SecretOf(request, uri), cancellationToken).ConfigureAwait(false);
        }

        // Resume: the existing job is the one to continue; nothing new was added.
        var target = resolution == DuplicateAction.Resume && check.ExistingJob is { } existing ? existing.Id : job.Id;
        var stored = await jobs.GetAsync(target, cancellationToken).ConfigureAwait(false);
        if (request.StartNow && stored is not null)
        {
            await engine.StartJobAsync(target, cancellationToken).ConfigureAwait(false);
        }

        return new AddResult(check, stored);
    }

    public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => jobs.GetAsync(id, cancellationToken);

    public async Task<LinkRefreshResult> RefreshLinkAsync(JobId id, string url, IReadOnlyList<JobCookie> cookies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A refreshed link must be an http or https address.", nameof(url));
        }

        var existing = secrets is null ? null : await secrets.GetAsync(id, cancellationToken).ConfigureAwait(false);
        var secret = (existing ?? new JobSecret { SensitiveOrigin = new Uri(uri.GetLeftPart(UriPartial.Authority)) }) with { Cookies = cookies };
        var settled = new TaskCompletionSource<LinkRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The engine's call returns when the transfer ends, which can be hours away. "Resumed" is the first bytes (or a finished file);
        // a pause or failure before that is the refusal.
        void OnEvent(EngineEvent engineEvent)
        {
            switch (engineEvent)
            {
                case JobProgressed progressed when progressed.Id == id:
                case JobStateChanged { To: JobState.Completed } changed when changed.Id == id:
                    settled.TrySetResult(new LinkRefreshResult(RefreshProblem.None));
                    break;
                case JobStateChanged { From: JobState.Downloading, To: JobState.Paused } paused when paused.Id == id:
                    settled.TrySetResult(new LinkRefreshResult(RefreshProblem.Rejected));
                    break;
                case JobStateChanged { To: JobState.Failed } failed when failed.Id == id:
                    settled.TrySetResult(new LinkRefreshResult(RefreshProblem.Failed));
                    break;
            }
        }

        engine.EventRaised += OnEvent;
        try
        {
            var run = Task.Run(() => engine.RefreshLinkAsync(id, uri, secret, CancellationToken.None), CancellationToken.None);
            _ = run.ContinueWith(
                task => settled.TrySetResult(new LinkRefreshResult(task.Exception?.GetBaseException() switch
                {
                    null when task.Result is TransferFailed { ErrorKind: TransferErrorKind.FileChanged } => RefreshProblem.Rejected,
                    null when task.Result is TransferFailed => RefreshProblem.Failed,
                    null => RefreshProblem.None,
                    InvalidOperationException or NotSupportedException => RefreshProblem.NotResumable,
                    _ => RefreshProblem.Failed,
                })),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try
            {
                return await settled.Task.WaitAsync(_resumeWait, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new LinkRefreshResult(RefreshProblem.None); // started and still validating: nothing refused it
            }
        }
        finally
        {
            engine.EventRaised -= OnEvent;
        }
    }

    public long? SpeedLimit
    {
        get => bandwidth?.GlobalBytesPerSecond;
        set => bandwidth?.SetGlobalRate(value);
    }

    public async Task PauseAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var job in await jobs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (job.State is JobState.Downloading or JobState.Probing or JobState.Retrying)
            {
                await TryAsync(() => engine.PauseJobAsync(job.Id, cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    public async Task ResumeAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var job in await jobs.ListAsync(state: JobState.Paused, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (job.PauseReason != PauseReason.AuthRequired)
            {
                await TryAsync(() => engine.ResumeJobAsync(job.Id, cancellationToken)).ConfigureAwait(false);
            }
        }
    }

    // one job that changed state meanwhile must not stop the rest
    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // it was no longer in the state we saw
        }
    }

    public Task StartAsync(JobId id, CancellationToken cancellationToken = default) => engine.StartJobAsync(id, cancellationToken);

    public Task PauseAsync(JobId id, CancellationToken cancellationToken = default) => engine.PauseJobAsync(id, cancellationToken);

    public Task SetJobOptionsAsync(JobId id, long? speedLimitBps, bool openWhenCompleted, CancellationToken cancellationToken = default) =>
        engine.SetJobOptionsAsync(id, speedLimitBps, openWhenCompleted, cancellationToken);

    public Task ResumeAsync(JobId id, CancellationToken cancellationToken = default) => engine.ResumeJobAsync(id, cancellationToken);

    public Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default) =>
        engine.CancelJobAsync(id, deleteFiles, cancellationToken);

    private static bool HasSecret(NewDownload request) => request.Cookies is { Count: > 0 } || !string.IsNullOrEmpty(request.UserAgent);

    private static JobSecret SecretOf(NewDownload request, Uri uri) => new()
    {
        Cookies = request.Cookies ?? [],
        Headers = string.IsNullOrEmpty(request.UserAgent)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["User-Agent"] = request.UserAgent },
        SensitiveOrigin = new Uri(uri.GetLeftPart(UriPartial.Authority)),
    };

    private async Task<(int? CategoryId, bool OnlyFallback)> ResolveCategoryAsync(Uri uri, NewDownload request, CancellationToken cancellationToken)
    {
        if (categoryRules is null)
        {
            return (null, false);
        }

        var all = await categories.ListAsync(cancellationToken).ConfigureAwait(false);
        var rules = await categoryRules.ListAsync(cancellationToken).ConfigureAwait(false);
        var match = CategoryResolver.Resolve(uri, request.FileName, request.Mime, rules, all);
        return (match?.Category.Id, match?.IsFallback ?? false);
    }

    private async Task<string> FolderForAsync(int? categoryId, CancellationToken cancellationToken)
    {
        var category = categoryId is { } id ? await categories.GetAsync(id, cancellationToken).ConfigureAwait(false) : null;
        return CategoryFolder.For(downloadsFolder, category);
    }

    public Task StartQueueAsync(int queueId, CancellationToken cancellationToken = default) =>
        queueRunner?.StartQueueAsync(queueId, cancellationToken) ?? Task.CompletedTask;

    public Task StopQueueAsync(int queueId, CancellationToken cancellationToken = default) =>
        queueRunner?.StopQueueAsync(queueId, cancellationToken) ?? Task.CompletedTask;

    public Task<QueueSchedule?> GetScheduleAsync(int queueId, CancellationToken cancellationToken = default) =>
        schedules?.GetAsync(queueId, cancellationToken) ?? Task.FromResult<QueueSchedule?>(null);

    public Task SaveScheduleAsync(QueueSchedule schedule, CancellationToken cancellationToken = default) =>
        schedules?.UpsertAsync(schedule, cancellationToken) ?? Task.CompletedTask;

    public async Task SaveQueueAsync(Core.Queue queue, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.Id > 0)
        {
            await queues.UpdateAsync(queue, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await queues.AddAsync(queue, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool WireRunner(DownloadEngine engine, IQueueRunner? runner)
    {
        if (runner is not null)
        {
            engine.EventRaised += evt =>
            {
                if (evt is JobStateChanged stateChanged)
                {
                    _ = runner.OnJobStateChangedAsync(stateChanged.Id, stateChanged.To);
                }
            };
        }
        return true;
    }
}
