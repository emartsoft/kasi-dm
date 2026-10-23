using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.App.Engine;
using Kasi.App.Resources;

namespace Kasi.App.ViewModels;

/// <summary>A heading ("STATUS") or something to click ("Active", a category, a queue) in the sidebar.</summary>
internal sealed partial class SidebarEntry : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int? _count;

    public SidebarEntry(string label, JobFilter? filter = null, int? count = null)
    {
        Label = label;
        Filter = filter;
        _count = count;
    }

    public string Label { get; }

    /// <summary>What clicking shows; null for a heading.</summary>
    public JobFilter? Filter { get; }

    public bool IsHeader => Filter is null;

    public string CountText => Count is { } n ? n.ToString(CultureInfo.CurrentCulture) : string.Empty;

    public override string ToString() => Label;
}

/// <summary>
/// The left side of the main window (wireframe): status filters with their counts, then the categories, then the queues. One entry is
/// selected at a time and drives <see cref="JobListViewModel.SetFilter"/>; a heading cannot be selected.
/// </summary>
internal sealed partial class SidebarViewModel : ObservableObject
{
    private readonly JobListViewModel _list;
    private readonly IJobService _jobs;
    private readonly SidebarEntry _all;
    private readonly SidebarEntry _active;
    private readonly SidebarEntry _finished;
    private readonly SidebarEntry _unfinished;
    private SidebarEntry _lastChoice;

    [ObservableProperty]
    private SidebarEntry? _selected;

    public SidebarViewModel(JobListViewModel list, IJobService jobs)
    {
        _list = list;
        _jobs = jobs;
        _all = new SidebarEntry(Strings.FilterAll, new JobFilter(StatusFilter.All));
        _active = new SidebarEntry(Strings.FilterActive, new JobFilter(StatusFilter.Active));
        _finished = new SidebarEntry(Strings.FilterFinished, new JobFilter(StatusFilter.Finished));
        _unfinished = new SidebarEntry(Strings.FilterUnfinished, new JobFilter(StatusFilter.Unfinished));
        Entries =
        [
            new SidebarEntry(Strings.SidebarStatus),
            _all,
            _active,
            _finished,
            _unfinished,
        ];
        _selected = _all;
        _lastChoice = _all;
        list.PropertyChanged += OnListChanged;
        UpdateCounts();
    }

    public ObservableCollection<SidebarEntry> Entries { get; }

    public string AutomationName => Strings.SidebarAutomationName;

    /// <summary>Adds the categories and the queues; the engine's service has them.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Entries.Add(new SidebarEntry(Strings.SidebarCategories));
        foreach (var category in await _jobs.CategoriesAsync(cancellationToken).ConfigureAwait(true))
        {
            Entries.Add(new SidebarEntry(category.Name, new JobFilter(CategoryId: category.Id)));
        }

        Entries.Add(new SidebarEntry(Strings.SidebarQueues));
        foreach (var queue in await _jobs.QueuesAsync(cancellationToken).ConfigureAwait(true))
        {
            Entries.Add(new SidebarEntry(queue.Name, new JobFilter(QueueId: queue.Id)));
        }
    }

    partial void OnSelectedChanged(SidebarEntry? value)
    {
        if (value?.Filter is { } filter)
        {
            _lastChoice = value;
            _list.SetFilter(filter);
        }
        else if (value is not null)
        {
            Selected = _lastChoice; // a heading is not a place to be
        }
    }

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JobListViewModel.AllCount) or nameof(JobListViewModel.ActiveCount) or nameof(JobListViewModel.FinishedCount) or nameof(JobListViewModel.UnfinishedCount))
        {
            UpdateCounts();
        }
    }

    private void UpdateCounts()
    {
        _all.Count = _list.AllCount;
        _active.Count = _list.ActiveCount;
        _finished.Count = _list.FinishedCount;
        _unfinished.Count = _list.UnfinishedCount;
    }
}
