namespace Kasi.Engine;

/// <summary>
/// Cancels a read that delivers nothing for <c>timeout</c> (ADR 0004: the scheduler cannot rescue a stalled connection, so the
/// engine abandons it). One guard per lease: <see cref="Reset"/> before every read restarts the countdown without allocating.
/// </summary>
internal sealed class StallGuard : IDisposable
{
    private readonly CancellationTokenSource _stall;
    private readonly CancellationTokenSource _linked;
    private readonly TimeSpan _timeout;
    private readonly CancellationToken _outer;

    public StallGuard(TimeProvider time, TimeSpan timeout, CancellationToken outer)
    {
        _timeout = timeout;
        _outer = outer;
        _stall = new CancellationTokenSource(Timeout.InfiniteTimeSpan, time);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _stall.Token);
    }

    /// <summary>The token to pass to the read: it fires when the caller cancels or the connection stalls.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>True when the read was cancelled by the stall timer and not by the caller.</summary>
    public bool Stalled => _stall.IsCancellationRequested && !_outer.IsCancellationRequested;

    public void Reset()
    {
        if (!_stall.IsCancellationRequested)
        {
            _stall.CancelAfter(_timeout);
        }
    }

    /// <summary>Stops the read timer while a worker is intentionally waiting for bandwidth or writing to disk.</summary>
    public void Pause()
    {
        if (!_stall.IsCancellationRequested)
        {
            _stall.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _linked.Dispose();
        _stall.Dispose();
    }
}
