using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.ViewModels;

/// <summary>What opens the Add dialog: a link typed or launched, or a download the browser handed over. Holds cookies, so it prints only its type.</summary>
internal sealed class AddDownloadDraft(string url, string? fileName = null, string? referrer = null, IReadOnlyList<JobCookie>? cookies = null, string? userAgent = null)
{
    public string Url { get; } = url;

    public string? FileName { get; } = fileName;

    public string? Referrer { get; } = referrer;

    public IReadOnlyList<JobCookie> Cookies { get; } = cookies ?? [];

    public string? UserAgent { get; } = userAgent;

    public override string ToString() => nameof(AddDownloadDraft);
}

internal sealed record ChecksumChoice(string? Algorithm, string Label);

/// <summary>
/// The Add Download dialog (KASI-UI-002, TRD 12.1): URL, file name, folder, category, queue, connections, start now or queue, checksum and
/// "remember for this site". <see cref="BuildRequest"/> maps the fields one to one onto the request; the add command sends it, asks
/// <c>askDuplicate</c> when the engine reports a conflict, and closes on success.
/// </summary>
internal sealed partial class AddDownloadViewModel : ObservableObject
{
    public const int MaxConnections = 32;

    private static readonly CompositeFormat _connectionsError = CompositeFormat.Parse(Strings.AddErrorConnections);

    private readonly IJobService _jobs;
    private readonly ISiteDefaults _sites;
    private readonly Func<DuplicateCheckResult, Task<DuplicateAction?>> _askDuplicate;
    private readonly AddDownloadDraft _draft;

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private string _fileName;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private Category? _selectedCategory;

    [ObservableProperty]
    private Core.Queue? _selectedQueue;

    [ObservableProperty]
    private decimal? _connections;

    [ObservableProperty]
    private bool _startNow = true;

    [ObservableProperty]
    private ChecksumChoice _selectedChecksum;

    [ObservableProperty]
    private string _checksumHex = string.Empty;

    [ObservableProperty]
    private bool _rememberForSite;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private bool _busy;

    public AddDownloadViewModel(
        AddDownloadDraft draft,
        IJobService jobs,
        ISiteDefaults sites,
        Func<DuplicateCheckResult, Task<DuplicateAction?>> askDuplicate)
    {
        ArgumentNullException.ThrowIfNull(draft);
        _draft = draft;
        _jobs = jobs;
        _sites = sites;
        _askDuplicate = askDuplicate;
        _url = draft.Url;
        _fileName = draft.FileName ?? DownloadFileName.Resolve(draft.Url);
        ChecksumChoices =
        [
            new(null, Strings.AddChecksumNone),
            new("md5", "MD5"),
            new("sha1", "SHA-1"),
            new("sha256", "SHA-256"),
        ];
        _selectedChecksum = ChecksumChoices[0];
    }

    public ObservableCollection<Category> Categories { get; } = [];

    public ObservableCollection<Core.Queue> Queues { get; } = [];

    public IReadOnlyList<ChecksumChoice> ChecksumChoices { get; }

    /// <summary>The job that was added; null until then.</summary>
    public Job? Result { get; private set; }

    /// <summary>Raised when the dialog should close (added or cancelled).</summary>
    public event Action? Closed;

    /// <summary>Loads the categories and queues, and picks the remembered category for the link's site, else General and the first queue.</summary>
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

        var remembered = Host() is { } host ? _sites.Get(host) : null;
        SelectedCategory = Categories.FirstOrDefault(c => c.Id == remembered?.CategoryId) ?? Categories.FirstOrDefault();
        SelectedQueue = Queues.FirstOrDefault();
        if (remembered?.Folder is { Length: > 0 } folder)
        {
            Folder = folder;
        }
    }

    /// <summary>The fields as a request, or null with <see cref="Error"/> set when one is not valid.</summary>
    public NewDownload? BuildRequest()
    {
        var url = Url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
        {
            Error = Strings.AddErrorUrl;
            return null;
        }

        if (string.IsNullOrWhiteSpace(FileName) || FileName.Trim() != FileNameSanitiser.Sanitise(FileName.Trim()))
        {
            Error = Strings.AddErrorFileName;
            return null;
        }

        if (SelectedQueue is null)
        {
            Error = Strings.AddErrorQueue;
            return null;
        }

        if (Connections is { } connections && (connections < 1 || connections > MaxConnections || connections != decimal.Truncate(connections)))
        {
            Error = string.Format(CultureInfo.CurrentCulture, _connectionsError, MaxConnections);
            return null;
        }

        var algorithm = SelectedChecksum.Algorithm;
        var hex = ChecksumHex.Trim();
        if ((algorithm is null) != (hex.Length == 0) || (algorithm is not null && !ExpectedChecksum.TryParse(algorithm, hex, out _)))
        {
            Error = Strings.AddErrorChecksum;
            return null;
        }

        Error = string.Empty;
        return new NewDownload(
            url,
            FileName.Trim(),
            SelectedQueue.Id,
            SelectedCategory?.Id,
            string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
            _draft.Referrer,
            Connections is { } c ? (int)c : null,
            StartNow,
            algorithm,
            algorithm is null ? null : hex,
            _draft.Cookies.Count > 0 ? _draft.Cookies : null,
            _draft.UserAgent);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var request = BuildRequest();
        if (request is null || Busy)
        {
            return;
        }

        Busy = true;
        try
        {
            var result = await _jobs.AddAsync(request).ConfigureAwait(true);
            if (result.Job is null)
            {
                var action = await _askDuplicate(result.Check).ConfigureAwait(true);
                if (action is null)
                {
                    return; // the user backed out of the prompt: the dialog stays open
                }

                result = await _jobs.AddAsync(request, action).ConfigureAwait(true);
            }

            if (RememberForSite && Host() is { } host)
            {
                await _sites.RememberAsync(host, new SiteChoice(SelectedCategory?.Id, request.Folder)).ConfigureAwait(true);
            }

            Result = result.Job;
            Closed?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            Error = Strings.AddErrorFailed;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();

    private string? Host() => Uri.TryCreate(Url.Trim(), UriKind.Absolute, out var uri) ? uri.Host : null;

    public string Title => Strings.AddTitle;

    public string AutomationName => Strings.AddAutomationName;

    public string UrlLabel => Strings.AddUrlLabel;

    public string FileNameLabel => Strings.AddFileNameLabel;

    public string FolderLabel => Strings.AddFolderLabel;

    public string CategoryLabel => Strings.AddCategoryLabel;

    public string QueueLabel => Strings.AddQueueLabel;

    public string ConnectionsLabel => Strings.AddConnectionsLabel;

    public string StartNowLabel => Strings.AddStartNowLabel;

    public string ChecksumLabel => Strings.AddChecksumLabel;

    public string ChecksumHexLabel => Strings.AddChecksumHexLabel;

    public string RememberLabel => Strings.AddRememberLabel;

    public string AddLabel => Strings.AddAddButton;

    public string CancelLabel => Strings.AddCancelButton;
}
