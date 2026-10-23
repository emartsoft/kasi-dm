using Kasi.Data;
using Kasi.Engine;
using Kasi.Scheduling;
using Microsoft.Extensions.Hosting;

namespace Kasi.App.Engine;

/// <summary>
/// Brings the database to the current schema, starts the checkpoint writer, the engine, and the scheduler when the host starts,
/// and stops them in reverse order. It runs after both pipes are open (ADR 0023), so a slow migration never delays the browser host's first answer.
/// </summary>
internal sealed class EngineLifetime(
    MigrationRunner migrations,
    CheckpointWriter checkpoints,
    DownloadEngine engine,
    QueueScheduler? scheduler = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrations.MigrateAsync(cancellationToken).ConfigureAwait(false);
        checkpoints.Start();
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
        if (scheduler is not null)
        {
            await scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        scheduler?.Dispose();
        await engine.StopAsync(cancellationToken).ConfigureAwait(false);
        await checkpoints.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
