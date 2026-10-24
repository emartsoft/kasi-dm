using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.Engine;

/// <summary>The queue runner's one control seam: HTTP/FTP jobs go to the engine, media jobs to <see cref="MediaJobExecutor"/>.</summary>
internal sealed class RoutingJobControl(
    DownloadEngine downloads,
    IJobRepository jobs,
    MediaJobExecutor media) : IJobControl
{
    public async Task StartJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The job does not exist.");
        if (job.Kind == JobKind.Media)
        {
            await media.StartJobAsync(id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await downloads.StartJobAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default)
    {
        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The job does not exist.");
        if (job.Kind == JobKind.Media)
        {
            await media.PauseJobAsync(id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await downloads.PauseJobAsync(id, cancellationToken).ConfigureAwait(false);
        }
    }
}
