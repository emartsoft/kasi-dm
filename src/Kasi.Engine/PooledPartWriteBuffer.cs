using System.Buffers;
using System.Runtime.CompilerServices;

namespace Kasi.Engine;

/// <summary>
/// Result of advancing or flushing a <see cref="PooledPartWriteBuffer"/>. <see cref="FlushedBytes"/> is positive only when a disk
/// write succeeded, so the caller passes it to <c>SegmentScheduler.AdvanceLease</c> only on success (ADR 0005).
/// </summary>
public readonly record struct PartBufferFlushResult(DiskStatus Status, int FlushedBytes)
{
    public bool IsSuccess => Status.IsSuccess;
}

/// <summary>
/// Per-worker coalescing buffer backed by <see cref="ArrayPool{T}.Shared"/> (TRD 6.4, ADR 0005). Rents a 256 KiB array when a
/// lease starts and returns it when the lease completes, is superseded or is abandoned, so idle workers hold no buffer against
/// the memory budget (<c>KASI-NFR-004</c>). Flushes when 256 KiB accumulates, when the oldest buffered byte reaches
/// <see cref="MaxBufferAge"/> (default 500 ms), or when the lease ends.
/// </summary>
public sealed class PooledPartWriteBuffer : IDisposable
{
    public static readonly TimeSpan DefaultMaxBufferAge = TimeSpan.FromMilliseconds(500);

    private readonly PartFileWriter _writer;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maxBufferAge;
    private readonly int _capacity;

    private byte[]? _buffer;
    private long _windowStartOffset;
    private long _leaseEnd;
    private int _bufferedBytes;
    private long _oldestByteTimestamp;
    private bool _disposed;

    public PooledPartWriteBuffer(
        PartFileWriter writer,
        TimeSpan? maxBufferAge = null,
        TimeProvider? timeProvider = null,
        int bufferCapacity = PartFileWriter.WriteBufferSize)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferCapacity);

        var age = maxBufferAge ?? DefaultMaxBufferAge;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(age, TimeSpan.Zero, nameof(maxBufferAge));

        _writer = writer;
        _maxBufferAge = age;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capacity = bufferCapacity;
    }

    public TimeSpan MaxBufferAge => _maxBufferAge;

    public int Capacity => _capacity;

    public bool HasActiveLease { get; private set; }

    public bool IsArrayRented => _buffer is not null;

    public long WindowStartOffset => _windowStartOffset;

    public int BufferedBytes => _bufferedBytes;

    public long NextReadOffset => _windowStartOffset + _bufferedBytes;

    public long LeaseEnd => _leaseEnd;

    public long RemainingLeaseBytes => HasActiveLease ? Math.Max(0L, _leaseEnd - NextReadOffset) : 0L;

    /// <summary>
    /// Slice of the rented array available for the next <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/> call,
    /// capped at both the 256 KiB buffer capacity and the current lease end so a network read never crosses <see cref="LeaseEnd"/>.
    /// </summary>
    public Memory<byte> AvailableWindow
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!HasActiveLease || _buffer is null)
            {
                return Memory<byte>.Empty;
            }

            var freeInBuffer = _capacity - _bufferedBytes;
            var remainingInLease = _leaseEnd - (_windowStartOffset + _bufferedBytes);
            var length = (int)Math.Min(freeInBuffer, remainingInLease);
            return length <= 0 ? Memory<byte>.Empty : _buffer.AsMemory(_bufferedBytes, length);
        }
    }

    public void BeginLease(long leaseStart, long leaseEnd = long.MaxValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (HasActiveLease)
        {
            throw new InvalidOperationException("A lease is already active on this buffer.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(leaseStart);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseEnd, leaseStart);

        var effectiveEnd = _writer.TotalBytes is { } total
            ? Math.Min(leaseEnd, total)
            : leaseEnd;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(effectiveEnd, leaseStart, nameof(leaseEnd));

        _buffer ??= ArrayPool<byte>.Shared.Rent(_capacity);
        _windowStartOffset = leaseStart;
        _leaseEnd = effectiveEnd;
        _bufferedBytes = 0;
        _oldestByteTimestamp = 0;
        HasActiveLease = true;
    }

    public ValueTask<PartBufferFlushResult> AdvanceBufferedAsync(
        int bytesRead,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasActiveLease)
        {
            throw new InvalidOperationException("No lease is active on this buffer.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(bytesRead);
        var maxAllowed = AvailableWindow.Length;
        if (bytesRead > maxAllowed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesRead),
                bytesRead,
                $"Bytes read ({bytesRead}) exceeds AvailableWindow ({maxAllowed}).");
        }

        if (bytesRead > 0)
        {
            if (_bufferedBytes == 0)
            {
                _oldestByteTimestamp = _timeProvider.GetTimestamp();
            }

            _bufferedBytes += bytesRead;
        }

        if (_bufferedBytes == 0)
        {
            return ValueTask.FromResult(new PartBufferFlushResult(DiskStatus.Success, 0));
        }

        var bufferFull = _bufferedBytes >= _capacity;
        var reachedLeaseEnd = _windowStartOffset + _bufferedBytes >= _leaseEnd;
        var ageExpired = _timeProvider.GetElapsedTime(_oldestByteTimestamp) >= _maxBufferAge;

        if (bufferFull || reachedLeaseEnd || ageExpired)
        {
            return FlushPendingAsync(cancellationToken);
        }

        return ValueTask.FromResult(new PartBufferFlushResult(DiskStatus.Success, 0));
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<PartBufferFlushResult> FlushPendingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasActiveLease || _bufferedBytes == 0)
        {
            if (HasActiveLease && _windowStartOffset >= _leaseEnd)
            {
                ReleaseLeaseCore();
            }

            return new PartBufferFlushResult(DiskStatus.Success, 0);
        }

        var count = _bufferedBytes;
        var status = await _writer.WriteAsync(_buffer!.AsMemory(0, count), _windowStartOffset, cancellationToken).ConfigureAwait(false);
        if (!status.IsSuccess)
        {
            // Failure contract (ADR 0005): failed write advances 0 bytes; caller does not advance the lease.
            return new PartBufferFlushResult(status, 0);
        }

        _windowStartOffset += count;
        _bufferedBytes = 0;
        _oldestByteTimestamp = 0;

        if (_windowStartOffset >= _leaseEnd)
        {
            ReleaseLeaseCore();
        }

        return new PartBufferFlushResult(DiskStatus.Success, count);
    }

    public async ValueTask<PartBufferFlushResult> CompleteLeaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!HasActiveLease)
        {
            return new PartBufferFlushResult(DiskStatus.Success, 0);
        }

        var result = await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            ReleaseLeaseCore();
        }

        return result;
    }

    /// <summary>
    /// Drops any buffered bytes without writing them to disk and returns the rented array to <see cref="ArrayPool{T}.Shared"/>
    /// (used when an endgame lease is superseded or a lease is aborted on error/pause).
    /// </summary>
    public void DropSuperseded()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReleaseLeaseCore();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ReleaseLeaseCore();
        _disposed = true;
    }

    private void ReleaseLeaseCore()
    {
        HasActiveLease = false;
        _bufferedBytes = 0;
        _windowStartOffset = 0;
        _leaseEnd = 0;
        _oldestByteTimestamp = 0;

        if (_buffer is { } rented)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
