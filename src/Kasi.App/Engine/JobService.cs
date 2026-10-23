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
    TimeProvider? timeProvider = null) : IJobService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public event Action<EngineEvent>? EventRaised
    {
        add => engine.EventRaised += value;
        remove => engine.EventRaised -= value;
    }

    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) => jobs.ListAsync(cancellationToken: cancellationToken);

    public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default) => categories.ListAsync(cancellationToken);

    public Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default) => queues.ListAsync(cancellationToken);

    public async Task<AddResult> AddAsync(NewDownload request, DuplicateAction? resolution = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
        {
            throw new ArgumentException("Only http, https, ftp and ftps links can be added.", nameof(request));
        }

        var folder = request.Folder ?? await FolderForAsync(request.CategoryId, cancellationToken).ConfigureAwait(false);
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
            CategoryId = request.CategoryId,
            Connections = request.Connections,
            CreatedUtc = _time.GetUtcNow(),
        };

        var check = await engine.AddJobAsync(job, resolution, cancellationToken).ConfigureAwait(false);
        if (check.HasConflict && resolution is null)
        {
            return new AddResult(check, null);
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

    public Task PauseAsync(JobId id, CancellationToken cancellationToken = default) => engine.PauseJobAsync(id, cancellationToken);

    public Task ResumeAsync(JobId id, CancellationToken cancellationToken = default) => engine.ResumeJobAsync(id, cancellationToken);

    public Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default) =>
        engine.CancelJobAsync(id, deleteFiles, cancellationToken);

    private async Task<string> FolderForAsync(int? categoryId, CancellationToken cancellationToken)
    {
        var category = categoryId is { } id ? await categories.GetAsync(id, cancellationToken).ConfigureAwait(false) : null;
        return string.IsNullOrEmpty(category?.Folder) ? downloadsFolder : Path.Combine(downloadsFolder, category.Folder);
    }
}
