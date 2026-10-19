namespace Kasi.Engine;

/// <summary>Opens one bounded lease; the shared scheduler owns retry, checkpointing and finalisation.</summary>
internal interface ILeaseTransport
{
    Task<LeaseBody> OpenAsync(int workerId, LeaseAssignment lease, CancellationToken cancellationToken);
}

internal sealed class LeaseBody(Stream body, IDisposable owner) : IAsyncDisposable
{
    public Stream Body { get; } = body;

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Body.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (owner is IAsyncDisposable asyncOwner)
            {
                await asyncOwner.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                owner.Dispose();
            }
        }
    }
}
