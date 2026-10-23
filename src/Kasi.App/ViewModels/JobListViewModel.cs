using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.App.Threading;
using Kasi.Core;

namespace Kasi.App.ViewModels;

internal enum StatusFilter
{
    All,
    Active,
    Finished,
    Unfinished,
}

/// <summary>What the sidebar and the search box ask the list to show. A null id means "any".</summary>
internal sealed record JobFilter(StatusFilter Status = StatusFilter.All, int? CategoryId = null, int? QueueId = null, string Text = "")
{
    public bool IsEverything => Status == StatusFilter.All && CategoryId is null && QueueId is null && Text.Length == 0;

    public bool Matches(JobRowViewModel row) =>
        (Status switch
        {
            StatusFilter.Active => row.IsActive,
            StatusFilter.Finished => row.StateValue == JobState.Completed,
            StatusFilter.Unfinished => row.StateValue != JobState.Completed,
            _ => true,
        })
        && (CategoryId is null || row.CategoryId == CategoryId)
        && (QueueId is null || row.QueueId == QueueId)
        && (Text.Length == 0 || row.FileName.Contains(Text, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The jobs the engine knows, kept current from its events (KASI-UI-001, 008). Progress goes through a <see cref="UiBatchDispatcher{TKey, TUpdate}"/>
/// (the latest per job, every 250 ms); added, removed and state changes are rare and applied at once. <see cref="Visible"/> is replaced as a
/// whole when the filter or the membership changes (one list rebuild, not a thousand collection events), and a row that only progresses just
/// raises its own property changes. Events arrive on the engine's thread, so <c>post</c> moves everything to the UI thread.
/// </summary>
internal sealed partial class JobListViewModel : ObservableObject, IAsyncDisposable
{
    public static readonly TimeSpan ProgressPeriod = TimeSpan.FromMilliseconds(250);

    private static readonly CompositeFormat _totalSpeedFormat = CompositeFormat.Parse(Strings.StatusTotalSpeed);

    private readonly IJobService _jobs;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly UiBatchDispatcher<JobId, JobProgressed> _progress;
    private readonly List<JobRowViewModel> _all = [];
    private readonly Dictionary<JobId, JobRowViewModel> _byId = [];
    private readonly Dictionary<JobId, long> _moving = [];
    private Dictionary<int, string> _categoryNames = [];
    private JobFilter _filter = new();

    [ObservableProperty]
    private IReadOnlyList<JobRowViewModel> _visible = [];

    [ObservableProperty]
    private int _allCount;

    [ObservableProperty]
    private int _activeCount;

    [ObservableProperty]
    private int _finishedCount;

    [ObservableProperty]
    private int _unfinishedCount;

    [ObservableProperty]
    private int _queuedCount;

    [ObservableProperty]
    private string _totalSpeedText = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public JobListViewModel(IJobService jobs, Action<Action>? post = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        _jobs = jobs;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        _time = timeProvider ?? TimeProvider.System;
        _progress = new UiBatchDispatcher<JobId, JobProgressed>(
            updates => _post(() => ApplyProgress(updates)),
            ProgressPeriod,
            timeProvider);
    }

    /// <summary>Every job, whatever the filter.</summary>
    public IReadOnlyList<JobRowViewModel> All => _all;

    public JobFilter Filter => _filter;

    public JobRowViewModel? Find(JobId id) => _byId.GetValueOrDefault(id);

    /// <summary>Sets what the sidebar picked; the search text stays.</summary>
    public void SetFilter(JobFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _filter = filter with { Text = SearchText.Trim() };
        Refilter();
    }

    partial void OnSearchTextChanged(string value)
    {
        _filter = _filter with { Text = value.Trim() };
        Refilter();
    }

    /// <summary>Loads the stored jobs, then follows the engine. Call once, on the UI thread.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _jobs.EventRaised += OnEvent;
        _categoryNames = (await _jobs.CategoriesAsync(cancellationToken).ConfigureAwait(true)).ToDictionary(c => c.Id, c => c.Name);
        foreach (var job in await _jobs.ListAsync(cancellationToken).ConfigureAwait(true))
        {
            AddRow(job);
        }

        Recount();
        Refilter();
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
                _post(() =>
                {
                    if (AddRow(added.Job))
                    {
                        Recount();
                        Refilter();
                    }
                });
                break;
            case JobStateChanged changed:
                _post(() =>
                {
                    if (_byId.TryGetValue(changed.Id, out var row))
                    {
                        row.StateChanged(changed.To);
                        if (changed.To != JobState.Downloading)
                        {
                            _moving.Remove(changed.Id);
                            UpdateTotalSpeed();
                        }

                        Recount();
                        if (!_filter.IsEverything)
                        {
                            Refilter(); // the row may have moved in or out of "Active" or "Finished"
                        }
                    }
                });
                break;
            case JobRemoved removed:
                _post(() =>
                {
                    if (_byId.Remove(removed.Id, out var row))
                    {
                        _all.Remove(row);
                        _moving.Remove(removed.Id);
                        Recount();
                        Refilter();
                        UpdateTotalSpeed();
                    }
                });
                break;
        }
    }

    private bool AddRow(Job job)
    {
        if (_byId.ContainsKey(job.Id))
        {
            return false; // the initial load and the event for the same job can both arrive
        }

        var row = new JobRowViewModel(job, job.CategoryId is { } id ? _categoryNames.GetValueOrDefault(id, string.Empty) : string.Empty, _time.GetUtcNow());
        _byId[job.Id] = row;
        _all.Add(row);
        return true;
    }

    private void Refilter() => Visible = _filter.IsEverything ? _all : [.. _all.Where(_filter.Matches)];

    private void Recount()
    {
        var active = 0;
        var finished = 0;
        var queued = 0;
        foreach (var row in _all)
        {
            active += row.IsActive ? 1 : 0;
            finished += row.StateValue == JobState.Completed ? 1 : 0;
            queued += row.StateValue == JobState.Queued ? 1 : 0;
        }

        AllCount = _all.Count;
        ActiveCount = active;
        FinishedCount = finished;
        UnfinishedCount = _all.Count - finished;
        QueuedCount = queued;
    }

    private void ApplyProgress(IReadOnlyCollection<JobProgressed> updates)
    {
        foreach (var update in updates)
        {
            if (_byId.TryGetValue(update.Id, out var row))
            {
                row.Progressed(update.DoneBytes, update.TotalBytes, update.BytesPerSecond);
                if (row.BytesPerSecond > 0)
                {
                    _moving[update.Id] = row.BytesPerSecond;
                }
                else
                {
                    _moving.Remove(update.Id);
                }
            }
        }

        UpdateTotalSpeed();
    }

    private void UpdateTotalSpeed()
    {
        var total = _moving.Values.Sum();
        TotalSpeedText = total <= 0 ? string.Empty : string.Format(CultureInfo.CurrentCulture, _totalSpeedFormat, ByteText.Format(total));
    }
}
