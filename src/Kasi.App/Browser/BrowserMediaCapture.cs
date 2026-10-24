using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Ipc.Messages;
using Kasi.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using IpcMediaKind = Kasi.Ipc.Messages.MediaKind;
using MediaKind = Kasi.Media.MediaKind;

namespace Kasi.App.Browser;

/// <summary>
/// The M3.5 capture path: parse with the page's context, refuse DRM, or add a media job with the default quality and start it.
/// Direct media is a normal HTTP download, not a media job (M3 plan decision 3).
/// </summary>
internal sealed partial class BrowserMediaCapture(
    MediaSourceLoader loader,
    IJobRepository jobs,
    IJobSecretRepository secrets,
    IJobControl control,
    IJobService jobService,
    ICategoryRepository categories,
    ICategoryRuleRepository categoryRules,
    Action<EngineEvent> publish,
    string downloadsFolder,
    TimeProvider? timeProvider = null,
    ILogger? logger = null,
    IMediaPicker? picker = null) : IBrowserMediaCapture
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Optional detection metadata (title/poster) supplied by the browser services after start-up.</summary>
    public Func<CaptureMedia, (string Title, string? ThumbnailUrl)>? Metadata { get; set; }

    public async Task<MediaCaptureResult> CaptureAsync(CaptureMedia message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Kind == IpcMediaKind.Direct)
        {
            return await AddDirectAsync(message, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var context = new MediaRequestContext(
                message.Cookies.Select(cookie => new JobCookie
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Domain = cookie.Domain,
                    Path = cookie.Path,
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                }).ToArray(),
                new Uri(message.Referrer),
                message.UserAgent,
                TryUri(message.Origin));
            var kind = message.Kind == IpcMediaKind.Dash ? MediaKind.Dash : MediaKind.Hls;
            var loaded = await loader.LoadAsync(kind, new Uri(message.Url), context, cancellationToken).ConfigureAwait(false);
            if (loaded is MediaSourceLoader.ProtectedResult)
            {
                LogProtected();
                return new MediaCaptureResult(MediaCaptureStatus.Protected, MediaSourceLoader.ProtectedMessage);
            }

            if (await FindActiveAsync(message.Url, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                LogDuplicate(existing.Id); // the same stream clicked twice is one capture
                return new MediaCaptureResult(MediaCaptureStatus.Accepted);
            }

            var presentation = ((MediaSourceLoader.Playable)loaded).Presentation;
            MediaPickerSelection? selection = null;
            if (picker is not null)
            {
                var (title, thumbnail) = Metadata?.Invoke(message) ?? (new Uri(message.Url).Host, null);
                selection = await picker.PickAsync(presentation, title, thumbnail, cancellationToken).ConfigureAwait(false);
                if (selection is null)
                {
                    return new MediaCaptureResult(MediaCaptureStatus.Cancelled);
                }
            }

            var id = JobId.New(_time);
            var fileName = CaptureFileName(message.Url, id, selection?.Format);
            var (categoryId, folder) = await PlaceAsync(message.Url, fileName, cancellationToken).ConfigureAwait(false);
            var queue = await jobs.ListAsync(1, cancellationToken: cancellationToken).ConfigureAwait(false);
            var job = new Job
            {
                Id = id,
                Kind = JobKind.Media,
                Url = message.Url,
                Referrer = message.Referrer,
                FileName = fileName,
                Folder = folder,
                QueueId = 1,
                QueuePos = queue.Select(item => item.QueuePos).DefaultIfEmpty(0).Max() + 1,
                CategoryId = categoryId,
                CreatedUtc = _time.GetUtcNow(),
            };
            await jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            publish(new JobAdded(job)); // the grid learns about a job from this event
            await secrets.SetAsync(id, SecretOf(message, selection), cancellationToken).ConfigureAwait(false);
            try
            {
                await control.StartJobAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogStartFailed(id, exception.GetType().Name); // the job is in the list, queued: the queue or the user can start it
            }

            LogAccepted(id);
            return new MediaCaptureResult(MediaCaptureStatus.Accepted);
        }
        catch (MediaLoadException exception)
        {
            LogLoadFailed(exception);
            return new MediaCaptureResult(MediaCaptureStatus.Failed, "The media link could not be read.");
        }
        catch (MediaParseException exception)
        {
            LogParseFailed(exception);
            return new MediaCaptureResult(MediaCaptureStatus.Failed, "The media link is not a supported stream.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCaptureFailed(exception);
            return new MediaCaptureResult(MediaCaptureStatus.Failed, "The media capture could not be started.");
        }
    }

    private async Task<Job?> FindActiveAsync(string url, CancellationToken cancellationToken) =>
        (await jobs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(job => job.Kind == JobKind.Media && job.Url == url && job.State is not (JobState.Completed or JobState.Failed));

    /// <summary>The category rules choose the folder, exactly as they do for any other download (a person's rules, KASI-CAT-001).</summary>
    private async Task<(int? CategoryId, string Folder)> PlaceAsync(string url, string fileName, CancellationToken cancellationToken)
    {
        var all = await categories.ListAsync(cancellationToken).ConfigureAwait(false);
        var rules = await categoryRules.ListAsync(cancellationToken).ConfigureAwait(false);
        var match = CategoryResolver.Resolve(new Uri(url), fileName, "video/mp4", rules, all);
        return (match?.Category.Id, CategoryFolder.For(downloadsFolder, match?.Category));
    }

    private async Task<MediaCaptureResult> AddDirectAsync(CaptureMedia message, CancellationToken cancellationToken)
    {
        try
        {
            var result = await jobService.AddAsync(new NewDownload(
                message.Url,
                FileNameFromUrl(message.Url),
                1,
                Referrer: message.Referrer,
                Cookies: message.Cookies.Select(cookie => new JobCookie
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Domain = cookie.Domain,
                    Path = cookie.Path,
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                }).ToArray(),
                UserAgent: message.UserAgent), cancellationToken: cancellationToken).ConfigureAwait(false);
            return result.Job is null
                ? new MediaCaptureResult(MediaCaptureStatus.Failed, "A download for this link already exists.")
                : new MediaCaptureResult(MediaCaptureStatus.Accepted);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogDirectFailed(exception);
            return new MediaCaptureResult(MediaCaptureStatus.Failed, "The download could not be started.");
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Media capture refused: protected stream.")]
    private partial void LogProtected();

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Media capture accepted as job {Job}.")]
    private partial void LogAccepted(Kasi.Core.JobId job);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "Media capture ignored: job {Job} already captures this stream.")]
    private partial void LogDuplicate(Kasi.Core.JobId job);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media job {Job} was added but could not start ({Kind}).")]
    private partial void LogStartFailed(Kasi.Core.JobId job, string kind);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media capture could not load the manifest.")]
    private partial void LogLoadFailed(MediaLoadException exception);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media capture could not parse the manifest.")]
    private partial void LogParseFailed(MediaParseException exception);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media capture failed.")]
    private partial void LogCaptureFailed(Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Direct media capture failed.")]
    private partial void LogDirectFailed(Exception exception);

    private static JobSecret SecretOf(CaptureMedia message, MediaPickerSelection? selection)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = message.UserAgent,
        };
        if (TryUri(message.Origin) is { } origin)
        {
            headers["Origin"] = origin.GetLeftPart(UriPartial.Authority);
        }

        if (selection?.Variant is { } selected)
        {
            headers["X-Kasi-Media-Variant"] = selected.Video.Id;
            headers["X-Kasi-Media-Format"] = selection.Format.ToString();
        }
        else if (selection?.AudioOnly is { } audio)
        {
            headers["X-Kasi-Media-Audio"] = audio.Id;
            headers["X-Kasi-Media-Format"] = selection.Format.ToString();
        }

        return new JobSecret
        {
            Cookies = message.Cookies.Select(cookie => new JobCookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = cookie.Path,
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
            }).ToArray(),
            Headers = headers,
        };
    }

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private static string CaptureFileName(string url, JobId id, MediaPickerFormat? format)
    {
        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "capture";
        }

        var extension = format switch
        {
            MediaPickerFormat.Matroska => ".mkv",
            MediaPickerFormat.AudioOnly => ".m4a",
            _ => ".mp4",
        };
        return $"{stem}-{id.ToString()[..8]}{extension}";
    }

    private static string FileNameFromUrl(string url)
    {
        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }
}
