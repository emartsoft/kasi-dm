using System.Diagnostics;

namespace Kasi.TestServer.Chaos;

/// <summary>Wraps the response body and applies the byte-level faults of a profile: reset, stall, throttle and truncation.</summary>
internal sealed class ChaosStream(Stream inner, HttpContext context, ChaosSession session, ConnectionInfo? connection) : Stream
{
    private const int ProbabilityBlock = 64 * 1024;

    private readonly ChaosProfile _profile = session.Profile;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _sent;
    private bool _stalled;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private long? ThrottleRate =>
        _profile.ThrottleBytesPerSecond is { } rate && (_profile.ThrottleConnectionIndex is null || _profile.ThrottleConnectionIndex == connection?.Index)
            ? rate
            : null;

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            if (_profile.TruncateAfterBytes is { } cut && _sent >= cut)
            {
                return; // swallow the rest: the declared length is never reached
            }

            if (_profile.StallSeconds is { } stall && !_stalled && _sent >= _profile.StallAfterBytes)
            {
                _stalled = true;
                await inner.FlushAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(stall), cancellationToken);
            }

            var take = SliceLength(buffer.Length);
            await inner.WriteAsync(buffer[..take], cancellationToken);
            _sent += take;
            buffer = buffer[take..];

            if (ThrottleRate is { } rate)
            {
                await inner.FlushAsync(cancellationToken);
                var wait = TimeSpan.FromSeconds((double)_sent / rate) - _clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }
            }

            if (ShouldReset())
            {
                await inner.FlushAsync(cancellationToken);
                session.Aborted = true;
                context.Abort();
                throw new OperationCanceledException("Chaos: connection reset.");
            }
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override void Flush() => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    private bool ShouldReset() =>
        (_profile.ResetAfterBytes is { } at && _sent >= at)
        || (_profile.ResetProbabilityPer64KiB is { } probability && _sent % ProbabilityBlock == 0 && session.Rng.NextDouble() < probability);

    /// <summary>Largest slice that does not step over a point where a fault must fire.</summary>
    private int SliceLength(int available)
    {
        long take = available;
        if (_profile.ResetAfterBytes is { } resetAt)
        {
            take = Math.Min(take, resetAt - _sent);
        }

        if (_profile.TruncateAfterBytes is { } cut)
        {
            take = Math.Min(take, cut - _sent);
        }

        if (_profile.StallSeconds is not null && !_stalled && _profile.StallAfterBytes > _sent)
        {
            take = Math.Min(take, _profile.StallAfterBytes - _sent);
        }

        if (_profile.ResetProbabilityPer64KiB is not null)
        {
            take = Math.Min(take, ProbabilityBlock - (_sent % ProbabilityBlock));
        }

        if (ThrottleRate is { } rate)
        {
            take = Math.Min(take, Math.Clamp(rate / 20, 1, 16 * 1024));
        }

        return (int)Math.Max(take, 1);
    }
}
