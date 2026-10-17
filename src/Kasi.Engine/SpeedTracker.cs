namespace Kasi.Engine;

/// <summary>
/// A worker's smoothed speed (TRD 6.8): throughput sampled once a second, smoothed with an exponential moving average
/// (alpha 0.3). Time a worker spends without a lease is left out, so waiting for work does not drag the speed down. Not thread
/// safe; <see cref="SegmentScheduler"/> calls it under its lock.
/// </summary>
internal sealed class SpeedTracker
{
    public const double Alpha = 0.3;

    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private DateTimeOffset _windowStart;
    private DateTimeOffset _idleSince;
    private long _windowBytes;
    private double _average;
    private bool _started;
    private bool _idle = true;

    /// <summary>True once at least one full sample exists; before that the speed is unknown, not zero.</summary>
    public bool HasSample { get; private set; }

    /// <summary>Bytes per second, as of the last completed sample (a stalled worker decays towards zero as samples of zero arrive).</summary>
    public double Speed(DateTimeOffset now)
    {
        if (!_idle)
        {
            Roll(now);
        }

        return HasSample ? _average : 0;
    }

    /// <summary>The worker starts or continues transferring; the time since <see cref="Idle"/> does not count.</summary>
    public void Resume(DateTimeOffset now)
    {
        if (!_started)
        {
            _windowStart = now;
            _started = true;
        }
        else if (_idle)
        {
            _windowStart += now - _idleSince;
        }

        _idle = false;
    }

    /// <summary>The worker has no lease; its speed is frozen until <see cref="Resume"/>.</summary>
    public void Idle(DateTimeOffset now)
    {
        if (_idle)
        {
            return;
        }

        Roll(now);
        _idleSince = now;
        _idle = true;
    }

    public void Add(long bytes, DateTimeOffset now)
    {
        Roll(now);
        _windowBytes += bytes;
    }

    private void Roll(DateTimeOffset now)
    {
        var elapsed = now - _windowStart;
        if (elapsed < SampleInterval)
        {
            return;
        }

        var sample = _windowBytes / elapsed.TotalSeconds;
        _average = HasSample ? (Alpha * sample) + ((1 - Alpha) * _average) : sample;
        HasSample = true;
        _windowBytes = 0;
        _windowStart = now;
    }
}
