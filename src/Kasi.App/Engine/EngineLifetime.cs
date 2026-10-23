using Kasi.Data;
using Kasi.Engine;
using Microsoft.Extensions.Hosting;

namespace Kasi.App.Engine;

/// <summary>
/// Brings the database to the current schema, starts the checkpoint writer and the engine when the host starts, and stops them in the
/// other order. It runs after both pipes are open (ADR 0023), so a slow migration never delays the browser host's first answer.
/// </summary>
internal sealed class EngineLifetime(MigrationRunner migrations, CheckpointWriter checkpoints, DownloadEngine engine) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await migrations.MigrateAsync(cancellationToken).ConfigureAwait(false);
        checkpoints.Start();
        await engine.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await engine.StopAsync(cancellationToken).ConfigureAwait(false);
        await checkpoints.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
