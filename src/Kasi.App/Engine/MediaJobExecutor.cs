using System.Collections.Concurrent;
using Kasi.Core;
using Kasi.Engine;
using Kasi.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kasi.App.Engine;

/// <summary>
/// Runs the media-specific part of a job: load the manifest with the saved page context, download/decrypt/join, mux with the
/// configured FFmpeg, verify the duration and move the job to Completed (M3.5). It starts in the background, like DownloadEngine,
/// and publishes every state change and progress step through <c>publish</c> (the engine's dispatcher), so the grid, tray,
/// notifications and the queue runner treat a media job like any other.
/// </summary>
internal sealed partial class MediaJobExecutor(
    IJobRepository jobs,
    IJobSecretRepository secrets,
    MediaJobRunner runner,
    MediaSourceLoader loader,
    BandwidthLimiter bandwidth,
    string mediaRoot,
    FfmpegOptions? ffmpeg,
    Action<EngineEvent> publish,
    TimeProvider? timeProvider = null,
    ILogger? logger = null) : IJobControl
{
    public const string FfmpegMissingMessage = "FFmpeg is not available, so this stream can't be saved.";

    private const int MaxLogLines = 20;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
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

        if (_running.ContainsKey(id))
        {
            return;
        }

        if (!await MoveToDownloadingAsync(job, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The media job cannot start from its current state.");
        }

        StartRun(id);
    }

    /// <summary>
    /// Stops the download and parks the job as Paused. A job already muxing or verifying is left to finish: that step is short
    /// and interrupting it would only redo the join.
    /// </summary>
    public async Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        if (!_running.TryGetValue(id, out var run))
        {
            return;
        }

        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null || job.State is JobState.Muxing or JobState.Verifying)
        {
            return;
        }

        await run.CancelAsync().ConfigureAwait(false);
        if (job.State is JobState.Downloading or JobState.Retrying)
        {
            await ChangeAsync(id, job.State, JobState.Paused, null, PauseReason.UserRequested, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Start-up recovery for media jobs (the engine only recovers HTTP and FTP jobs): a job caught downloading is parked and
    /// requeued, a job caught muxing is muxed again from its joined files, and a job whose file was already saved is completed.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        foreach (var state in new[] { JobState.Probing, JobState.Downloading, JobState.Retrying, JobState.Verifying, JobState.Muxing })
        {
            var stuck = await jobs.ListAsync(state: state, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var job in stuck.Where(job => job.Kind == JobKind.Media))
            {
                await RecoverJobAsync(job, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RecoverJobAsync(Job job, CancellationToken cancellationToken)
    {
        switch (job.State)
        {
            case JobState.Probing:
                await ChangeAsync(job.Id, JobState.Probing, JobState.Queued, null, null, cancellationToken).ConfigureAwait(false);
                break;
            case JobState.Muxing:
                StartRun(job.Id); // the segments and joined files are on disk: RunAsync rejoins and muxes
                break;
            case JobState.Verifying when File.Exists(Path.Combine(job.Folder, job.FileName)):
                if (await ChangeAsync(job.Id, JobState.Verifying, JobState.Completed, null, null, cancellationToken).ConfigureAwait(false))
                {
                    await secrets.DeleteAsync(job.Id, cancellationToken).ConfigureAwait(false);
                }

                break;
            default: // Downloading, Retrying, or Verifying without its file
                if (await ChangeAsync(job.Id, job.State, JobState.Paused, null, PauseReason.CrashRecovery, cancellationToken).ConfigureAwait(false))
                {
                    await ChangeAsync(job.Id, JobState.Paused, JobState.Queued, null, null, cancellationToken).ConfigureAwait(false);
                }

                break;
        }
    }

    private void StartRun(JobId id)
    {
        var run = new CancellationTokenSource();
        if (!_running.TryAdd(id, run))
        {
            run.Dispose();
            return;
        }

        _ = Task.Run(() => RunAsync(id, run.Token), CancellationToken.None);
    }

    private async Task<bool> MoveToDownloadingAsync(Job job, CancellationToken cancellationToken)
    {
        switch (job.State)
        {
            case JobState.Downloading or JobState.Muxing:
                return true;
            case JobState.Queued:
                return await ChangeAsync(job.Id, JobState.Queued, JobState.Probing, null, null, cancellationToken).ConfigureAwait(false)
                    && await ChangeAsync(job.Id, JobState.Probing, JobState.Downloading, null, null, cancellationToken).ConfigureAwait(false);
            case JobState.Paused:
                return await ChangeAsync(job.Id, JobState.Paused, JobState.Downloading, null, null, cancellationToken).ConfigureAwait(false);
            default:
                return false;
        }
    }

    private async Task<bool> ChangeAsync(
        JobId id,
        JobState from,
        JobState to,
        string? error,
        PauseReason? pauseReason,
        CancellationToken cancellationToken)
    {
        if (!await jobs.TryChangeStateAsync(id, from, to, error, pauseReason, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        publish(new JobStateChanged(id, from, to, error));
        return true;
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

            if (ffmpeg is null)
            {
                LogFfmpegMissing(id);
                await FailAsync(job.Id, FfmpegMissingMessage).ConfigureAwait(false);
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
            if (result is not MediaSourceLoader.Playable playable)
            {
                await FailAsync(job.Id, MediaSourceLoader.ProtectedMessage).ConfigureAwait(false);
                return;
            }

            var downloaded = await runner.RunAsync(playable, new MediaJobOptions
            {
                JobId = id,
                ManifestUri = new Uri(job.Url),
                MediaRoot = mediaRoot,
                RequestContext = context,
                BandwidthLimiter = bandwidth,
                Events = publish,
                SelectedVariant = SelectedVariant(playable.Presentation, secret),
            }, cancellationToken).ConfigureAwait(false);

            var format = Header(secret, "X-Kasi-Media-Format");
            var muxed = await new MediaMuxer(new FfmpegRunner(ffmpeg)).MuxAsync(
                downloaded,
                new MediaMuxOptions
                {
                    OutputPath = Path.Combine(job.Folder, job.FileName),
                    Container = format switch
                    {
                        "Matroska" => MediaMuxContainer.Matroska,
                        "Mp4" => MediaMuxContainer.Mp4,
                        _ => MediaMuxContainer.Auto,
                    },
                },
                cancellationToken).ConfigureAwait(false);
            var current = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            await jobs.UpdateAsync(current with { FileName = Path.GetFileName(muxed.OutputPath) }, cancellationToken).ConfigureAwait(false);
            if (await ChangeAsync(id, JobState.Muxing, JobState.Verifying, null, null, cancellationToken).ConfigureAwait(false))
            {
                // Delete before the terminal transition so a Completed job never has a secret left to clean up.
                await secrets.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
                await ChangeAsync(id, JobState.Verifying, JobState.Completed, null, null, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // pause or shutdown: the state and the completion rows are already durable
        }
        catch (MediaMuxException exception)
        {
            LogJobFailed(id, exception.Kind.ToString());
            foreach (var line in (exception.SanitizedStderr ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxLogLines))
            {
                publish(new JobLogged(id, _time.GetUtcNow(), line));
            }

            await FailAsync(id, exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogJobFailed(id, exception.GetType().Name);
            await FailAsync(id, "The media capture failed.").ConfigureAwait(false);
        }
        finally
        {
            if (_running.TryRemove(id, out var run))
            {
                run.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media job {Job} cannot run: FFmpeg is not configured.")]
    private partial void LogFfmpegMissing(JobId job);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Media job {Job} failed ({Kind}).")]
    private partial void LogJobFailed(JobId job, string kind);

    private async Task FailAsync(JobId id, string message)
    {
        foreach (var state in new[] { JobState.Downloading, JobState.Muxing, JobState.Verifying, JobState.Retrying, JobState.Probing })
        {
            if (await ChangeAsync(id, state, JobState.Failed, message, null, CancellationToken.None).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static MediaVariant? SelectedVariant(MediaPresentation presentation, JobSecret? secret)
    {
        var variantId = Header(secret, "X-Kasi-Media-Variant");
        if (!string.IsNullOrEmpty(variantId))
        {
            return presentation.Variants.FirstOrDefault(variant => variant.Video.Id == variantId);
        }

        var audioId = Header(secret, "X-Kasi-Media-Audio");
        var track = string.IsNullOrEmpty(audioId) ? null : presentation.AudioOnly.FirstOrDefault(audio => audio.Id == audioId);
        return track is null ? null : new MediaVariant(track, track, null);
    }

    private static string? Header(JobSecret? secret, string name) =>
        secret?.Headers.TryGetValue(name, out var value) == true ? value : null;

    private static Uri? TryUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}
