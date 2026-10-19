namespace Kasi.Engine;

/// <summary>
/// Shared byte budget for network reads. One instance covers all jobs, and a job may also have its own cap
/// (KASI-BWL-001, KASI-BWL-002). The same global acquisition method can be used by a torrent adapter.
/// </summary>
public sealed class BandwidthLimiter
{
    public const int MaximumReadBytes = 16 * 1024;

    private readonly object _sync = new();
    private readonly TimeProvider _time;
    private readonly Bucket _global;
    private TaskCompletionSource _rateChanged = NewSignal();
    private int _waiters;

    public BandwidthLimiter(long? globalBytesPerSecond = null, TimeProvider? timeProvider = null)
    {
        ValidateRate(globalBytesPerSecond);
        _time = timeProvider ?? TimeProvider.System;
        _global = new Bucket(globalBytesPerSecond, _time.GetTimestamp());
    }

    public long? GlobalBytesPerSecond
    {
        get
        {
            lock (_sync)
            {
                return _global.Rate;
            }
        }
    }

    internal long ConsumedBytes
    {
        get
        {
            lock (_sync)
            {
                return _global.ConsumedBytes;
            }
        }
    }

    /// <summary>Changes the shared cap while transfers are running. Null means unlimited.</summary>
    public void SetGlobalRate(long? bytesPerSecond)
    {
        ValidateRate(bytesPerSecond);
        lock (_sync)
        {
            _global.SetRate(bytesPerSecond, _time.GetTimestamp(), _time);
            SignalRateChanged();
        }
    }

    /// <summary>Creates a budget shared by all connections of one job. Null means that only the global cap applies.</summary>
    public JobBandwidthLimit CreateJobLimit(long? bytesPerSecond = null)
    {
        ValidateRate(bytesPerSecond);
        return new JobBandwidthLimit(this, bytesPerSecond, _time.GetTimestamp());
    }

    /// <summary>
    /// Reserves up to 16 KiB from the global budget and, when supplied, the job budget. Call
    /// <see cref="ReturnUnused"/> for bytes the network read did not deliver.
    /// </summary>
    public ValueTask<int> AcquireAsync(int requestedBytes, JobBandwidthLimit? jobLimit = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedBytes);
        ValidateOwner(jobLimit);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Math.Min(requestedBytes, MaximumReadBytes);
        lock (_sync)
        {
            if (TryTake(target, jobLimit, out var granted, out _))
            {
                return ValueTask.FromResult(granted);
            }
        }

        return WaitAsync(target, jobLimit, cancellationToken);
    }

    /// <summary>Reserves bytes from the shared budget for traffic without an HTTP job, such as a future torrent adapter.</summary>
    public ValueTask<int> AcquireGlobalAsync(int requestedBytes, CancellationToken cancellationToken = default) =>
        AcquireAsync(requestedBytes, cancellationToken: cancellationToken);

    /// <summary>Returns a reservation's unused bytes after a short read, EOF, or failed read.</summary>
    public void ReturnUnused(int bytes, JobBandwidthLimit? jobLimit = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        ValidateOwner(jobLimit);
        if (bytes == 0)
        {
            return;
        }

        lock (_sync)
        {
            var now = _time.GetTimestamp();
            _global.Return(bytes, now, _time);
            jobLimit?.State.Return(bytes, now, _time);
        }
    }

    /// <summary>True when the global cap or the job cap is a number; false means reads can use the whole buffer window.</summary>
    public bool IsLimited(JobBandwidthLimit? jobLimit = null)
    {
        ValidateOwner(jobLimit);
        lock (_sync)
        {
            return _global.Rate is not null || jobLimit?.State.Rate is not null;
        }
    }

    internal bool Owns(JobBandwidthLimit jobLimit) => ReferenceEquals(jobLimit.Owner, this);

    private async ValueTask<int> WaitAsync(int target, JobBandwidthLimit? jobLimit, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan wait;
            Task changed;
            lock (_sync)
            {
                if (TryTake(target, jobLimit, out var granted, out wait))
                {
                    return granted;
                }

                changed = _rateChanged.Task;
                _waiters++;
            }

            try
            {
                using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var delay = Task.Delay(wait, _time, waitCancellation.Token);
                var first = await Task.WhenAny(delay, changed).ConfigureAwait(false);
                if (first == delay)
                {
                    await delay.ConfigureAwait(false);
                }
                else
                {
                    await waitCancellation.CancelAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_sync)
                {
                    _waiters--;
                }
            }
        }
    }

    // Both buckets are debited under one lock: a worker waiting on its job cap cannot hoard global tokens.
    private bool TryTake(int requested, JobBandwidthLimit? jobLimit, out int granted, out TimeSpan wait)
    {
        var now = _time.GetTimestamp();
        _global.Refill(now, _time);
        jobLimit?.State.Refill(now, _time);

        granted = Math.Min(requested, _global.MaximumGrant);
        if (jobLimit is not null)
        {
            granted = Math.Min(granted, jobLimit.State.MaximumGrant);
        }

        var globalWait = _global.WaitFor(granted);
        var jobWait = jobLimit?.State.WaitFor(granted) ?? TimeSpan.Zero;
        wait = globalWait > jobWait ? globalWait : jobWait;
        if (wait > TimeSpan.Zero)
        {
            return false;
        }

        _global.Take(granted);
        jobLimit?.State.Take(granted);
        return true;
    }

    private void SetJobRate(JobBandwidthLimit jobLimit, long? bytesPerSecond)
    {
        ValidateRate(bytesPerSecond);
        lock (_sync)
        {
            jobLimit.State.SetRate(bytesPerSecond, _time.GetTimestamp(), _time);
            SignalRateChanged();
        }
    }

    private void SignalRateChanged()
    {
        if (_waiters == 0)
        {
            return;
        }

        var old = _rateChanged;
        _rateChanged = NewSignal();
        old.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ValidateRate(long? rate)
    {
        if (rate is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "A bandwidth cap must be positive, or null for unlimited.");
        }
    }

    private void ValidateOwner(JobBandwidthLimit? jobLimit)
    {
        if (jobLimit is not null && !Owns(jobLimit))
        {
            throw new ArgumentException("The job budget belongs to a different bandwidth limiter.", nameof(jobLimit));
        }
    }

    public sealed class JobBandwidthLimit
    {
        internal JobBandwidthLimit(BandwidthLimiter owner, long? rate, long timestamp)
        {
            Owner = owner;
            State = new Bucket(rate, timestamp);
        }

        internal BandwidthLimiter Owner { get; }

        internal Bucket State { get; }

        internal long ConsumedBytes
        {
            get
            {
                lock (Owner._sync)
                {
                    return State.ConsumedBytes;
                }
            }
        }

        public long? BytesPerSecond
        {
            get
            {
                lock (Owner._sync)
                {
                    return State.Rate;
                }
            }
        }

        public void SetRate(long? bytesPerSecond) => Owner.SetJobRate(this, bytesPerSecond);
    }

    internal sealed class Bucket
    {
        private long _timestamp;
        private double _tokens;

        public Bucket(long? rate, long timestamp)
        {
            Rate = rate;
            _timestamp = timestamp;
            _tokens = 0;
        }

        public long? Rate { get; private set; }

        public long ConsumedBytes { get; private set; }

        public int MaximumGrant => Rate is { } value ? (int)Math.Min(MaximumReadBytes, Math.Floor(Capacity(value))) : MaximumReadBytes;

        public void Refill(long now, TimeProvider time)
        {
            if (Rate is { } rate)
            {
                var elapsed = time.GetElapsedTime(_timestamp, now).TotalSeconds;
                _tokens = Math.Min(Capacity(rate), _tokens + (elapsed * rate));
            }

            _timestamp = now;
        }

        public TimeSpan WaitFor(int bytes)
        {
            if (Rate is not { } rate || _tokens >= bytes)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds(Math.Max(0.001, (bytes - _tokens) / rate));
        }

        public void Take(int bytes)
        {
            ConsumedBytes += bytes;
            if (Rate is not null)
            {
                _tokens -= bytes;
            }
        }

        public void Return(int bytes, long now, TimeProvider time)
        {
            Refill(now, time);
            ConsumedBytes -= bytes;
            if (Rate is { } rate)
            {
                _tokens = Math.Min(Capacity(rate), _tokens + bytes);
            }
        }

        public void SetRate(long? rate, long now, TimeProvider time)
        {
            Refill(now, time);
            var wasUnlimited = Rate is null;
            Rate = rate;
            _tokens = rate is { } value && !wasUnlimited ? Math.Min(_tokens, Capacity(value)) : 0;
        }

        private static double Capacity(long rate) => Math.Max(1, rate / 4d);
    }
}
