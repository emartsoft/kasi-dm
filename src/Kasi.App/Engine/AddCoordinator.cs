using Kasi.App.Browser;
using Kasi.App.Startup;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.Engine;

/// <summary>The windows the coordinator needs, so it is tested without any.</summary>
internal interface IAddDialogHost
{
    /// <summary>Shows the Add dialog and returns when it closes.</summary>
    Task ShowAddAsync(AddDownloadViewModel dialog);

    /// <summary>Shows the duplicate prompt; null when the user cancels.</summary>
    Task<DuplicateAction?> AskDuplicateAsync(DuplicateCheckResult check);
}

/// <summary>
/// Where a link ends up as a job: from a second launch with URLs and from the browser. One Add dialog is open at a time; the rest wait
/// their turn. A browser download opens the dialog unless the user switched that off (decision 5 of the M2.8 plan), in which case it is
/// added at once with the defaults and a duplicate gets a new name. Link batches go on to the picker (<paramref name="picker"/>).
/// </summary>
internal sealed class AddCoordinator(
    IJobService jobs,
    ISiteDefaults sites,
    IBrowserSettings settings,
    IAddDialogHost host,
    IBrowserRequestSink picker,
    Action<Exception>? onFault = null) : IBrowserRequestSink, IAddDownloadService
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;

    public void ShowAdd(BrowserDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        var draft = new AddDownloadDraft(
            download.FinalUrl,
            string.IsNullOrWhiteSpace(download.Filename) ? null : download.Filename,
            download.Referrer,
            [.. download.Cookies.Select(c => new JobCookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Secure = c.Secure, HttpOnly = c.HttpOnly })],
            download.UserAgent);
        Run(settings.Current.ShowAddDialog ? () => OpenAsync(draft) : () => AddDirectAsync(draft));
    }

    public void ShowPicker(BrowserBatch batch) => picker.ShowPicker(batch);

    public void RequestAdd(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            var draft = new AddDownloadDraft(url);
            Run(() => OpenAsync(draft));
        }
    }

    /// <summary>Starts the work on its own and reports a failure to <c>onFault</c>; never throws to the pipe or the UI thread.</summary>
    private void Run(Func<Task> work)
    {
        lock (_gate)
        {
            _tail = RunAfterAsync(_tail, work);
        }
    }

    // One at a time, in the order asked. The previous link never faults: a failure is reported and swallowed below.
    private async Task RunAfterAsync(Task previous, Func<Task> work)
    {
#pragma warning disable VSTHRD003 // the previous link of this coordinator's own chain, started by Run
        await previous.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        try
        {
            await Task.Run(work).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onFault?.Invoke(ex);
        }
    }

    private async Task OpenAsync(AddDownloadDraft draft)
    {
        var dialog = new AddDownloadViewModel(draft, jobs, sites, host.AskDuplicateAsync);
        await dialog.LoadAsync().ConfigureAwait(false);
        await host.ShowAddAsync(dialog).ConfigureAwait(false);
    }

    private async Task AddDirectAsync(AddDownloadDraft draft)
    {
        var queue = (await jobs.QueuesAsync().ConfigureAwait(false))[0];
        var site = Uri.TryCreate(draft.Url, UriKind.Absolute, out var uri) ? sites.Get(uri.Host) : null;
        var request = new NewDownload(
            draft.Url,
            draft.FileName ?? DownloadFileName.Resolve(draft.Url),
            queue.Id,
            site?.CategoryId ?? 1,
            site?.Folder,
            draft.Referrer,
            Cookies: draft.Cookies.Count > 0 ? draft.Cookies : null,
            UserAgent: draft.UserAgent);
        var result = await jobs.AddAsync(request).ConfigureAwait(false);
        if (result.Job is null)
        {
            await jobs.AddAsync(request, DuplicateAction.AutoRename).ConfigureAwait(false);
        }
    }
}
