namespace Kasi.Engine;

/// <summary>
/// The transfer-wide pause a throttling server (<c>429</c>, or <c>503</c> with <c>Retry-After</c>) imposes (KASI-RES-004).
/// Workers wait out <see cref="Remaining"/> before their next request, and only the first throttle of a window asks for a
/// connection reduction, so eight workers hitting <c>429</c> together lower the pool by one, not by eight.
/// </summary>
internal sealed class ThrottleGate(TimeProvider time)
{
    private long _untilTicks;

    /// <summary>Starts or extends the window by <paramref name="delay"/>; true when this call began a new window.</summary>
    public bool Enter(TimeSpan delay)
    {
        var now = time.GetUtcNow().UtcTicks;
        var until = now + delay.Ticks;
        while (true)
        {
            var current = Volatile.Read(ref _untilTicks);
            var isNew = now >= current;
            if (Interlocked.CompareExchange(ref _untilTicks, Math.Max(current, until), current) == current)
            {
                return isNew;
            }
        }
    }

    /// <summary>How long a worker must still wait before it may send a request; zero outside a window.</summary>
    public TimeSpan Remaining()
    {
        var left = Volatile.Read(ref _untilTicks) - time.GetUtcNow().UtcTicks;
        return left > 0 ? TimeSpan.FromTicks(left) : TimeSpan.Zero;
    }
}
