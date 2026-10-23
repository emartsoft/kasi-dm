using System.Collections.ObjectModel;
using Avalonia.Threading;
using Kasi.App.Engine;
using Kasi.App.Threading;
using Kasi.Core;

namespace Kasi.App.ViewModels;

/// <summary>
/// The jobs the engine knows, kept current from its events. Progress goes through a <see cref="UiBatchDispatcher{TKey, TUpdate}"/> (the
/// latest per job, every 250 ms); added, removed and state changes are rare and applied at once. Events arrive on the engine's thread, so
/// <c>post</c> moves everything to the UI thread (a test passes a direct call).
/// </summary>
internal sealed class JobListViewModel : IAsyncDisposable
{
    public static readonly TimeSpan ProgressPeriod = TimeSpan.FromMilliseconds(250);

    private readonly IJobService _jobs;
    private readonly Action<Action> _post;
    private readonly UiBatchDispatcher<JobId, JobProgressed> _progress;
    private readonly Dictionary<JobId, JobRowViewModel> _byId = [];

    public JobListViewModel(IJobService jobs, Action<Action>? post = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _jobs = jobs;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _progress = new UiBatchDispatcher<JobId, JobProgressed>(
            updates => _post(() => ApplyProgress(updates)),
            ProgressPeriod,
            timeProvider);
    }

    public ObservableCollection<JobRowViewModel> Rows { get; } = [];

    /// <summary>Loads the stored jobs, then follows the engine. Call once, on the UI thread.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _jobs.EventRaised += OnEvent;
        foreach (var job in await _jobs.ListAsync(cancellationToken).ConfigureAwait(true))
        {
            Add(job);
        }

        _progress.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.EventRaised -= OnEvent;
        await _progress.DisposeAsync().ConfigureAwait(false);
    }

    private void OnEvent(EngineEvent engineEvent)
    {
        switch (engineEvent)
        {
            case JobProgressed progressed:
                _progress.Post(progressed.Id, progressed);
                break;
            case JobAdded added:
                _post(() => Add(added.Job));
                break;
            case JobStateChanged changed:
                _post(() =>
                {
                    if (_byId.TryGetValue(changed.Id, out var row))
                    {
                        row.StateChanged(changed.To);
                    }
                });
                break;
            case JobRemoved removed:
                _post(() =>
                {
                    if (_byId.Remove(removed.Id, out var row))
                    {
                        Rows.Remove(row);
                    }
                });
                break;
        }
    }

    private void Add(Job job)
    {
        if (_byId.ContainsKey(job.Id))
        {
            return; // the initial load and the event for the same job can both arrive
        }

        var row = new JobRowViewModel(job);
        _byId[job.Id] = row;
        Rows.Add(row);
    }

    private void ApplyProgress(IReadOnlyCollection<JobProgressed> updates)
    {
        foreach (var update in updates)
        {
            if (_byId.TryGetValue(update.Id, out var row))
            {
                row.Progressed(update.DoneBytes, update.TotalBytes);
            }
        }
    }
}
