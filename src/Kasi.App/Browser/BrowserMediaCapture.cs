using Kasi.App.Engine;
using Kasi.Core;
using Kasi.Ipc.Messages;
using IpcMediaKind = Kasi.Ipc.Messages.MediaKind;
using MediaKind = Kasi.Media.MediaKind;
using Kasi.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    string downloadsFolder,
    TimeProvider? timeProvider = null,
    ILogger? logger = null) : IBrowserMediaCapture
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

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

            var id = JobId.New(_time);
            var queue = await jobs.ListAsync(1, cancellationToken: cancellationToken).ConfigureAwait(false);
            var job = new Job
            {
                Id = id,
                Kind = JobKind.Media,
                Url = message.Url,
                Referrer = message.Referrer,
                FileName = CaptureFileName(message.Url, id),
                Folder = downloadsFolder,
                QueueId = 1,
                QueuePos = queue.Select(item => item.QueuePos).DefaultIfEmpty(0).Max() + 1,
                CreatedUtc = _time.GetUtcNow(),
            };
            await jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
            await secrets.SetAsync(id, SecretOf(message), cancellationToken).ConfigureAwait(false);
            await control.StartJobAsync(id, cancellationToken).ConfigureAwait(false);
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

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media capture could not load the manifest.")]
    private partial void LogLoadFailed(MediaLoadException exception);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media capture could not parse the manifest.")]
    private partial void LogParseFailed(MediaParseException exception);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media capture failed.")]
    private partial void LogCaptureFailed(Exception exception);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Direct media capture failed.")]
    private partial void LogDirectFailed(Exception exception);

    private static JobSecret SecretOf(CaptureMedia message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["User-Agent"] = message.UserAgent,
        };
        if (TryUri(message.Origin) is { } origin)
        {
            headers["Origin"] = origin.GetLeftPart(UriPartial.Authority);
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

    private static string CaptureFileName(string url, JobId id)
    {
        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "capture";
        }

        return $"{stem}-{id.ToString()[..8]}.mp4";
    }

    private static string FileNameFromUrl(string url)
    {
        var name = Path.GetFileName(new Uri(url).AbsolutePath);
        return string.IsNullOrWhiteSpace(name) ? "download" : name;
    }
}
