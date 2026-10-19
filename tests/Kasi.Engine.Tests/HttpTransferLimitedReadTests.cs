using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

/// <summary>
/// What a limiter changes about reading a response body (KASI-BWL-001, KASI-NFR-007, ADR 0010): nothing when there is no cap,
/// 16 KiB slices under a cap, and every slice written before the next wait so a throttled worker never sits on received bytes.
/// </summary>
[Trait("Requirement", "KASI-BWL-001")]
[Trait("Requirement", "KASI-NFR-007")]
[Trait("TC", "TC-ENG-024")]
public sealed class HttpTransferLimitedReadTests : IDisposable
{
    private const int Window = 256 * 1024;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-limited-read-" + Guid.NewGuid().ToString("N"));

    public HttpTransferLimitedReadTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public async Task Without_a_cap_a_read_asks_for_the_whole_window()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var limiter in new BandwidthLimiter?[] { null, new BandwidthLimiter() })
        {
            using var body = new ScriptedStream(Window);

            await HttpTransferRunner.ReadLimitedAsync(body, new byte[Window], guard: null, limiter, jobLimit: null, ct);

            body.RequestedLengths.Should().Equal(Window);
        }
    }

    [Fact]
    public async Task Under_a_cap_a_read_is_at_most_sixteen_kib()
    {
        var ct = TestContext.Current.CancellationToken;
        var limiter = new BandwidthLimiter(100_000_000);
        using var body = new ScriptedStream(Window);

        await HttpTransferRunner.ReadLimitedAsync(body, new byte[Window], guard: null, limiter, jobLimit: null, ct);

        body.RequestedLengths.Should().Equal(BandwidthLimiter.MaximumReadBytes);
    }

    [Fact]
    public async Task A_job_cap_alone_also_limits_the_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var limiter = new BandwidthLimiter();
        var job = limiter.CreateJobLimit(100_000_000);
        using var body = new ScriptedStream(Window);

        await HttpTransferRunner.ReadLimitedAsync(body, new byte[Window], guard: null, limiter, job, ct);

        body.RequestedLengths.Should().Equal(BandwidthLimiter.MaximumReadBytes);
    }

    [Fact]
    public async Task Under_a_cap_every_received_slice_is_written_before_the_next_wait()
    {
        var written = await BytesWrittenWhileBlockedAsync(new BandwidthLimiter(100_000_000));

        written.Should().Be(2000, "the two slices received before the worker blocked must already be on disk");
    }

    [Fact]
    public async Task Without_a_cap_slices_are_coalesced_in_the_buffer_as_before()
    {
        var written = await BytesWrittenWhileBlockedAsync(limiter: null);

        written.Should().Be(0, "an uncapped transfer keeps filling the 256 KiB buffer until it is full or old");
    }

    // Reads two 1000-byte slices, then blocks inside the third read; returns what the scheduler counts as written meanwhile.
    private async Task<long> BytesWrittenWhileBlockedAsync(BandwidthLimiter? limiter)
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 6000;
        var time = new FakeTimeProvider();
        var opened = PartFileWriter.TryOpen(_folder, "limited.bin", size).Should().BeOfType<PartFileOpened>().Subject;
        using var writer = opened.Writer;
        var scheduler = new SegmentScheduler(size, rangesSupported: true, new SegmentSchedulerOptions { MaxConnections = 1, InitialConnections = 1 });
        var lease = scheduler.TryAcquireNextLease(0)!.Value;
        using var buffer = new PooledPartWriteBuffer(writer, TimeSpan.FromMilliseconds(500), time);
        buffer.BeginLease(lease.Start, lease.End);
        using var stallGuard = new StallGuard(time, TimeSpan.FromSeconds(30), ct);
        using var body = new ScriptedStream(1000, 1000, 4000) { BlockAfterReads = 2 };
        var options = new HttpTransferOptions { BandwidthLimiter = limiter, TimeProvider = time };

        var running = HttpTransferRunner.ReadLeaseAsync(
            lease, body, buffer, scheduler, options, new HttpTransferRunner.LeaseProgress(), stallGuard, ct);
#pragma warning disable VSTHRD003 // the blocked signal belongs to the test double above
        await body.Blocked.WaitAsync(TimeSpan.FromSeconds(10), ct);
#pragma warning restore VSTHRD003
        var written = scheduler.TotalWritten;
        body.Release();
        await running.WaitAsync(TimeSpan.FromSeconds(10), ct);
        return written;
    }

    /// <summary>A body that serves zero bytes in the given chunks, records each requested length and can block after some reads.</summary>
    private sealed class ScriptedStream(params int[] chunks) : Stream
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public List<int> RequestedLengths { get; } = [];

        public int BlockAfterReads { get; init; } = int.MaxValue;

#pragma warning disable VSTHRD003 // the task is created and completed by this test double
        public Task Blocked => _blocked.Task;
#pragma warning restore VSTHRD003

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            RequestedLengths.Add(buffer.Length);
            if (_reads == BlockAfterReads)
            {
                _blocked.TrySetResult();
#pragma warning disable VSTHRD003 // the release signal is created and set by this test double
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }

            if (_reads >= chunks.Length)
            {
                return 0;
            }

            var count = Math.Min(chunks[_reads++], buffer.Length);
            buffer.Span[..count].Clear();
            return count;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
