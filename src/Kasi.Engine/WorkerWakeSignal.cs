namespace Kasi.Engine;

/// <summary>
/// Wakes idle transfer workers (M1.7b). At most one stored wake per worker is kept: a wake that nobody was waiting for is
/// remembered only up to that cap, so a long busy stretch cannot leave a backlog that makes idle workers spin through
/// thousands of immediate wake-ups, each taking the scheduler lock.
/// </summary>
internal sealed class WorkerWakeSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public WorkerWakeSignal(int workers)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        _semaphore = new SemaphoreSlim(0, workers);
    }

    public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);

    /// <summary>Wakes up to <paramref name="count"/> waiting workers; wakes beyond the cap are dropped.</summary>
    public void Wake(int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            try
            {
                _semaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                return; // the cap is stored already; a waiting worker would have consumed a permit
            }
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
