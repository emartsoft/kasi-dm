using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kasi.App.Threading;

/// <summary>
/// Coalesces updates by key (the latest per job wins) and hands the set to <c>apply</c> once per period, so a fast engine cannot flood the
/// UI thread (TRD 12.2; 250 ms in the app). <c>apply</c> is called from the dispatcher's own loop: it posts to the UI thread itself.
/// The timer comes from the <see cref="TimeProvider"/>, so tests drive it with a fake clock.
/// </summary>
internal sealed partial class UiBatchDispatcher<TKey, TUpdate> : IAsyncDisposable
    where TKey : notnull
{
    private readonly Action<IReadOnlyCollection<TUpdate>> _apply;
    private readonly TimeSpan _period;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private Dictionary<TKey, TUpdate> _pending = [];
    private Task? _loop;

    public UiBatchDispatcher(Action<IReadOnlyCollection<TUpdate>> apply, TimeSpan period, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _apply = apply;
        _period = period;
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    public void Start()
    {
        if (_loop is not null)
        {
            throw new InvalidOperationException("The dispatcher was already started.");
        }

        // created here, not in the loop, so a tick that comes before the loop first waits is not lost
#pragma warning disable CA2000 // the loop owns the timer and disposes it
        var timer = new PeriodicTimer(_period, _time);
#pragma warning restore CA2000
        _loop = Task.Run(() => RunAsync(timer, _stopping.Token));
    }

    /// <summary>Records the latest update for a key; nothing is applied until the next tick.</summary>
    public void Post(TKey key, TUpdate update)
    {
        lock (_gate)
        {
            _pending[key] = update;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
#pragma warning disable VSTHRD003 // started by Start() on this dispatcher
            await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        using (timer)
        {
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    Dictionary<TKey, TUpdate> batch;
                    lock (_gate)
                    {
                        if (_pending.Count == 0)
                        {
                            continue;
                        }

                        batch = _pending;
                        _pending = [];
                    }

                    try
                    {
                        _apply(batch.Values);
                    }
                    catch (Exception ex)
                    {
                        // the type only: the message may name a job or a path
                        LogApplyFailed(ex.GetType().Name);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Applying a UI batch failed ({ExceptionType}); the next batch is applied as usual.")]
    private partial void LogApplyFailed(string exceptionType);
}
