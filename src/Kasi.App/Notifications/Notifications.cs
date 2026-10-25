using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.App.ViewModels;
using Kasi.Core;

namespace Kasi.App.Notifications;

/// <summary>Shows a toast. The text is the file name only: a toast is visible to anyone looking at the screen and is kept in the action centre.</summary>
internal interface INotificationService
{
    /// <param name="details">What the body adds under the name, for example "1.2 GB in 4 min"; null when the size or the time is not known.</param>
    void ShowCompleted(JobId id, string fileName, string? details = null);

    void ShowFailed(JobId id, string fileName);
}

/// <summary>What a toast button does to the file system, behind an interface so the router is tested without Explorer.</summary>
internal interface IShell
{
    /// <summary>Opens the file with the program Windows has for it. Does nothing if it is gone.</summary>
    void OpenFile(string path);

    /// <summary>Opens the folder in Explorer with the file selected; just the folder if the file is gone.</summary>
    void ShowInFolder(string path);

    /// <summary>Opens the folder itself in Explorer. Does nothing if it is gone.</summary>
    void OpenFolder(string path);
}

/// <summary>The words a toast puts in its activation arguments; the router reads the same.</summary>
internal static class ToastActions
{
    public const string Action = "action";
    public const string Job = "job";
    public const string OpenFile = "open-file";
    public const string OpenFolder = "open-folder";
}

/// <summary>
/// Maps the arguments of a clicked toast (<c>action=open-file&amp;job=&lt;id&gt;</c>) to the shell. Arguments come from outside the process
/// (anything can activate a toast's COM server), so only the two known actions run, only for a completed job Kasi knows, and the path is
/// rebuilt from the stored folder and file name, never taken from the arguments.
/// </summary>
internal sealed class NotificationRouter(IJobService jobs, IShell shell)
{
    public async Task<bool> HandleAsync(string? arguments, CancellationToken cancellationToken = default)
    {
        var values = Parse(arguments);
        if (!values.TryGetValue(ToastActions.Action, out var action)
            || !values.TryGetValue(ToastActions.Job, out var jobText)
            || !JobId.TryParse(jobText, out var id))
        {
            return false;
        }

        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is not { State: JobState.Completed })
        {
            return false;
        }

        var path = Path.Combine(job.Folder, job.FileName);
        switch (action)
        {
            case ToastActions.OpenFile:
                shell.OpenFile(path);
                return true;
            case ToastActions.OpenFolder:
                shell.ShowInFolder(path);
                return true;
            default:
                return false;
        }
    }

    private static Dictionary<string, string> Parse(string? arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (arguments ?? string.Empty).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
            {
                values[Uri.UnescapeDataString(pair[..at])] = Uri.UnescapeDataString(pair[(at + 1)..]);
            }
        }

        return values;
    }
}

/// <summary>Turns the engine's "completed" and "failed" into toasts that carry the file name and nothing else.</summary>
internal sealed class CompletionNotifier(IJobService jobs, INotificationService notifications, Action<Exception>? onFault = null, IShell? shell = null)
{
    private static readonly System.Text.CompositeFormat _detailsFormat = System.Text.CompositeFormat.Parse(Strings.ToastDetailsFormat);

    public void Watch() => jobs.EventRaised += OnEvent;

    private void OnEvent(EngineEvent engineEvent)
    {
        if (engineEvent is JobStateChanged { To: JobState.Completed or JobState.Failed } changed)
        {
            _ = NotifyAsync(changed.Id, changed.To);
        }
    }

    /// <summary>"1.2 GB in 4 min": the size and the time from adding to completing; null when either is not known.</summary>
    internal static string? CompletedDetails(Job job)
    {
        if (job.TotalBytes is not { } size || job.CompletedUtc is not { } done)
        {
            return null;
        }

        var seconds = Math.Max(1, (long)(done - job.CreatedUtc).TotalSeconds);
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, _detailsFormat, ByteText.Format(size), JobText.TimeLeft(seconds));
    }

    private async Task NotifyAsync(JobId id, JobState state)
    {
        try
        {
            if (await jobs.GetAsync(id).ConfigureAwait(false) is not { } job)
            {
                return;
            }

            if (state == JobState.Completed)
            {
                notifications.ShowCompleted(id, job.FileName, CompletedDetails(job));
                if (job.OpenWhenCompleted)
                {
                    shell?.OpenFile(Path.Combine(job.Folder, job.FileName));
                }
            }
            else
            {
                notifications.ShowFailed(id, job.FileName); // the error text can name a server or a path: not shown
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            onFault?.Invoke(ex);
        }
    }
}
