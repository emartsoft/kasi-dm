using System.Collections.Concurrent;
using Kasi.Core;
using Kasi.Engine;
using Kasi.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kasi.App.Engine;

/// <summary>
/// Runs the media-specific part of a job: load the manifest with the saved page context, download/decrypt/join, mux with the
/// configured FFmpeg, verify the duration and move the job to Completed (M3.5). It starts in the background, like DownloadEngine.
/// </summary>
internal sealed partial class MediaJobExecutor(
    IJobRepository jobs,
    IJobSecretRepository secrets,
    MediaJobRunner runner,
    MediaSourceLoader loader,
    BandwidthLimiter bandwidth,
    string mediaRoot,
    FfmpegOptions? ffmpeg,
    ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<JobId, CancellationTokenSource> _running = new();

    public async Task StartJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The media job does not exist.");
        if (job.Kind != JobKind.Media)
        {
            throw new InvalidOperationException("The job is not a media job.");
        }

        if (!await MoveToDownloadingAsync(job, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The media job cannot start from its current state.");
        }

        var run = new CancellationTokenSource();
        if (!_running.TryAdd(id, run))
        {
            run.Dispose();
            return;
        }

        _ = Task.Run(() => RunAsync(id, run.Token), CancellationToken.None);
    }

    public async Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(id, out var run))
        {
            await run.CancelAsync().ConfigureAwait(false);
        }

    }

    private async Task<bool> MoveToDownloadingAsync(Job job, CancellationToken cancellationToken)
    {
        if (job.State == JobState.Downloading || job.State == JobState.Muxing)
        {
            return true;
        }

        if (job.State != JobState.Queued)
        {
            return false;
        }

        if (!await jobs.TryChangeStateAsync(job.Id, JobState.Queued, JobState.Probing, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return await jobs.TryChangeStateAsync(job.Id, JobState.Probing, JobState.Downloading, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(JobId id, CancellationToken cancellationToken)
    {
        Job? job = null;
        try
        {
            job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                return;
            }

            var secret = await secrets.GetAsync(id, cancellationToken).ConfigureAwait(false);
            var context = new MediaRequestContext(
                secret?.Cookies ?? [],
                TryUri(job.Referrer),
                Header(secret, "User-Agent"),
                TryUri(Header(secret, "Origin")));
            var kind = job.Url.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase) ? MediaKind.Dash : MediaKind.Hls;
            var result = await loader.LoadAsync(kind, new Uri(job.Url), context, cancellationToken).ConfigureAwait(false);
            if (result is MediaSourceLoader.ProtectedResult)
            {
                await FailAsync(job, MediaSourceLoader.ProtectedMessage, cancellationToken).ConfigureAwait(false);
                return;
            }

            var playable = (MediaSourceLoader.Playable)result;
            var downloaded = await runner.RunAsync(playable, new MediaJobOptions
            {
                JobId = id,
                ManifestUri = new Uri(job.Url),
                MediaRoot = mediaRoot,
                RequestContext = context,
                BandwidthLimiter = bandwidth,

            }, cancellationToken).ConfigureAwait(false);

            if (ffmpeg is null)
            {
                LogFfmpegMissing(id);
                return;
            }

            var muxed = await new MediaMuxer(new FfmpegRunner(ffmpeg)).MuxAsync(
                downloaded,
                new MediaMuxOptions { OutputPath = Path.Combine(job.Folder, job.FileName) },
                cancellationToken).ConfigureAwait(false);
            var current = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            await jobs.UpdateAsync(current with { FileName = Path.GetFileName(muxed.OutputPath) }, cancellationToken).ConfigureAwait(false);
            await jobs.TryChangeStateAsync(id, JobState.Muxing, JobState.Verifying, cancellationToken: cancellationToken).ConfigureAwait(false);
            await jobs.TryChangeStateAsync(id, JobState.Verifying, JobState.Completed, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // pause/close: the Downloading state and the completion rows are already durable
        }
        catch (Exception exception)
        {
            LogJobFailed(id, exception.GetType().Name);
            if (job is not null)
            {
                await FailAsync(job, "The media capture failed.", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_running.TryRemove(id, out var run))
            {
                run.Dispose();
            }
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media job {Job} downloaded but FFmpeg is not configured; leaving it in Muxing.")]
    private partial void LogFfmpegMissing(Kasi.Core.JobId job);

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Media job {Job} failed ({Kind}).")]
    private partial void LogJobFailed(Kasi.Core.JobId job, string kind);

    private async Task FailAsync(Job job, string message, CancellationToken cancellationToken)
    {
        foreach (var state in new[] { JobState.Downloading, JobState.Muxing, JobState.Verifying })
        {
            if (await jobs.TryChangeStateAsync(job.Id, state, JobState.Failed, message, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static string? Header(JobSecret? secret, string name) =>
        secret?.Headers.TryGetValue(name, out var value) == true ? value : null;

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}
