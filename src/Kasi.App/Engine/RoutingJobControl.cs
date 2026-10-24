using Kasi.Core;

namespace Kasi.App.Engine;

/// <summary>The queue runner's one control seam: HTTP/FTP jobs go to the engine, media jobs to <see cref="MediaJobExecutor"/>.</summary>
internal sealed class RoutingJobControl(
    IJobControl downloads,
    IJobRepository jobs,
    IJobControl media) : IJobControl
{
    public async Task StartJobAsync(JobId id, CancellationToken cancellationToken = default) =>
        await ControlFor(await KindOfAsync(id, cancellationToken).ConfigureAwait(false)).StartJobAsync(id, cancellationToken).ConfigureAwait(false);

    public async Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default) =>
        await ControlFor(await KindOfAsync(id, cancellationToken).ConfigureAwait(false)).PauseJobAsync(id, cancellationToken).ConfigureAwait(false);

    private IJobControl ControlFor(JobKind kind) => kind == JobKind.Media ? media : downloads;

    private async Task<JobKind> KindOfAsync(JobId id, CancellationToken cancellationToken) =>
        (await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false))?.Kind
            ?? throw new InvalidOperationException("The job does not exist.");
}
