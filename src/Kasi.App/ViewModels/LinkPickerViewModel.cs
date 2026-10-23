using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.ViewModels;

/// <summary>One link of a batch. Shows the file name and the address (the user's own links, on the user's own screen; never logged).</summary>
internal sealed partial class LinkRowViewModel : ObservableObject
{
    private readonly Action<bool> _toggled;

    [ObservableProperty]
    private bool _isSelected;

    public LinkRowViewModel(string url, string fileName, string extension, Action<bool> toggled)
    {
        Url = url;
        FileName = fileName;
        Extension = extension;
        _toggled = toggled;
    }

    public string Url { get; }

    public string FileName { get; }

    /// <summary>Lowercase, without the dot; empty when the name has none.</summary>
    public string Extension { get; }

    public override string ToString() => nameof(LinkRowViewModel); // the address can carry a token

    partial void OnIsSelectedChanged(bool value) => _toggled(value);
}

internal sealed record ExtensionChoice(string? Extension, string Label);

/// <summary>
/// The link picker (KASI-BRW-005, TC-BRW-005 Kasi half): the links of a page chosen in the browser. Filter by extension and by text (it
/// matches the address and the file name), select the shown links or none, pick the destination and add one job per selected link, all
/// sharing the batch's referrer, cookies and user agent. A duplicate gets a new name (a prompt per link would be unusable at 40 links).
/// <see cref="Shown"/> is replaced as a whole, not edited, so a page of 100,000 links costs one list rebuild per filter change.
/// </summary>
internal sealed partial class LinkPickerViewModel : ObservableObject
{
    private readonly BrowserBatch _batch;
    private readonly IJobService _jobs;
    private readonly List<LinkRowViewModel> _all;
    private int _selected;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private ExtensionChoice _selectedExtension;

    [ObservableProperty]
    private IReadOnlyList<LinkRowViewModel> _shown;

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private Core.Queue? _selectedQueue;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private bool _startNow = true;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _busy;

    public LinkPickerViewModel(BrowserBatch batch, IJobService jobs)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _batch = batch;
        _jobs = jobs;
        _all = new List<LinkRowViewModel>(batch.Links.Count);
        foreach (var link in batch.Links)
        {
            var name = string.IsNullOrWhiteSpace(link.Filename) ? DownloadFileName.Resolve(link.Url) : link.Filename;
            _all.Add(new LinkRowViewModel(link.Url, name, ExtensionOf(name), OnRowToggled));
        }

        Extensions =
        [
            new(null, Strings.PickerAllExtensions),
            .. _all.Where(r => r.Extension.Length > 0)
                .GroupBy(r => r.Extension)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Take(50)
                .Select(g => new ExtensionChoice(g.Key, string.Create(CultureInfo.CurrentCulture, $".{g.Key} ({g.Count()})"))),
        ];
        _selectedExtension = Extensions[0];
        _shown = _all;
        UpdateSummary();
    }

    public IReadOnlyList<ExtensionChoice> Extensions { get; }

    public ObservableCollection<Category> Categories { get; } = [];

    public ObservableCollection<Core.Queue> Queues { get; } = [];

    public int TotalCount => _all.Count;

    public int SelectedCount => _selected;

    /// <summary>How many jobs were added; set when the add finished.</summary>
    public int AddedCount { get; private set; }

    /// <summary>Raised when the window should close.</summary>
    public event Action? Closed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        foreach (var category in await _jobs.CategoriesAsync(cancellationToken).ConfigureAwait(true))
        {
            Categories.Add(category);
        }

        foreach (var queue in await _jobs.QueuesAsync(cancellationToken).ConfigureAwait(true))
        {
            Queues.Add(queue);
        }

        SelectedCategory = Categories.FirstOrDefault();
        SelectedQueue = Queues.FirstOrDefault();
    }

    /// <summary>Selects every link that is shown (the filter decides which).</summary>
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var row in Shown)
        {
            row.IsSelected = true;
        }
    }

    /// <summary>Clears the selection of every link, shown or not.</summary>
    [RelayCommand]
    private void SelectNone()
    {
        foreach (var row in _all)
        {
            row.IsSelected = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        if (SelectedQueue is null)
        {
            Error = Strings.AddErrorQueue;
            return;
        }

        Busy = true;
        Error = string.Empty;
        var added = 0;
        var failed = 0;
        try
        {
            var folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim();
            var cookies = _batch.Cookies.Count > 0
                ? (IReadOnlyList<JobCookie>)[.. _batch.Cookies.Select(c => new JobCookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Secure = c.Secure, HttpOnly = c.HttpOnly })]
                : null;
            foreach (var row in _all.Where(r => r.IsSelected).ToList())
            {
                var request = new NewDownload(row.Url, row.FileName, SelectedQueue.Id, SelectedCategory?.Id, folder, _batch.Referrer, StartNow: StartNow, Cookies: cookies, UserAgent: _batch.UserAgent);
                try
                {
                    var result = await _jobs.AddAsync(request).ConfigureAwait(true);
                    if (result.Job is null)
                    {
                        result = await _jobs.AddAsync(request, DuplicateAction.AutoRename).ConfigureAwait(true);
                    }

                    added += result.Job is null ? 0 : 1;
                    failed += result.Job is null ? 1 : 0;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
                {
                    failed++;
                }
            }
        }
        finally
        {
            Busy = false;
        }

        AddedCount = added;
        if (failed > 0)
        {
            Error = string.Format(CultureInfo.CurrentCulture, _failedFormat, failed);
            return; // stay open: the user sees how many did not make it
        }

        Closed?.Invoke();
    }

    private bool CanAdd() => _selected > 0 && !Busy;

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();

    partial void OnFilterTextChanged(string value) => Refilter();

    partial void OnSelectedExtensionChanged(ExtensionChoice value) => Refilter();

    partial void OnBusyChanged(bool value) => AddCommand.NotifyCanExecuteChanged();

    private static readonly CompositeFormat _failedFormat = CompositeFormat.Parse(Strings.PickerFailed);
    private static readonly CompositeFormat _summaryFormat = CompositeFormat.Parse(Strings.PickerSummary);

    private static string ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 && dot < name.Length - 1 ? name[(dot + 1)..].ToLowerInvariant() : string.Empty;
    }

    private void Refilter()
    {
        var text = FilterText.Trim();
        var extension = SelectedExtension?.Extension;
        Shown = text.Length == 0 && extension is null
            ? _all
            : [.. _all.Where(r =>
                (extension is null || r.Extension == extension)
                && (text.Length == 0
                    || r.Url.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || r.FileName.Contains(text, StringComparison.OrdinalIgnoreCase)))];
        UpdateSummary();
    }

    private void OnRowToggled(bool selected)
    {
        _selected += selected ? 1 : -1;
        UpdateSummary();
        AddCommand.NotifyCanExecuteChanged();
    }

    private void UpdateSummary() =>
        Summary = string.Format(CultureInfo.CurrentCulture, _summaryFormat, _selected, _all.Count, Shown.Count);

    public string Title => Strings.PickerTitle;

    public string AutomationName => Strings.PickerAutomationName;

    public string FilterLabel => Strings.PickerFilterLabel;

    public string ExtensionLabel => Strings.PickerExtensionLabel;

    public string SelectAllLabel => Strings.PickerSelectAll;

    public string SelectNoneLabel => Strings.PickerSelectNone;

    public string CategoryLabel => Strings.AddCategoryLabel;

    public string QueueLabel => Strings.AddQueueLabel;

    public string FolderLabel => Strings.AddFolderLabel;

    public string StartNowLabel => Strings.AddStartNowLabel;

    public string AddLabel => Strings.PickerAdd;

    public string CancelLabel => Strings.AddCancelButton;
}
