using System.Security.Cryptography;
using Kasi.TestServer;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NonParallelAllocationGroup
{
    public const string Name = "NonParallelAllocation";
}

[Collection(NonParallelAllocationGroup.Name)]
[Trait("Requirement", "KASI-ENG-008")]
public sealed class PooledPartWriteBufferTests : IDisposable
{
    private readonly string _tempDir;

    public PooledPartWriteBufferTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kasi-buffer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Coalesces_16_KiB_chunks_into_256_KiB_writes_flushes_on_500_ms_age_and_tail_and_matches_SHA256()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        const string seed = "coalesce-seed";
        const int chunkSize = 16 * 1024;
        const int totalSize = (2 * PartFileWriter.WriteBufferSize) + (3 * chunkSize); // 560 KiB

        using var opened = PartFileWriter.TryOpen(_tempDir, "coalesced.bin", totalSize).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        var writeOffsets = new List<(long Offset, int Length)>();
        writer.WriteFaultInjector = (offset, length) =>
        {
            writeOffsets.Add((offset, length));
            return null;
        };

        using var buffer = new PooledPartWriteBuffer(writer, timeProvider: clock);
        buffer.IsArrayRented.Should().BeFalse();

        buffer.BeginLease(0, totalSize);
        buffer.IsArrayRented.Should().BeTrue();
        buffer.HasActiveLease.Should().BeTrue();

        long offsetCursor = 0;

        // 1. Feed 15 chunks of 16 KiB (240 KiB) with no clock movement -> 0 disk writes.
        for (var i = 0; i < 15; i++)
        {
            GeneratedContent.Fill(seed, offsetCursor, buffer.AvailableWindow.Span[..chunkSize]);
            var step = await buffer.AdvanceBufferedAsync(chunkSize, ct);
            step.IsSuccess.Should().BeTrue();
            step.FlushedBytes.Should().Be(0);
            offsetCursor += chunkSize;
        }

        writeOffsets.Should().BeEmpty();

        // 2. 16th chunk reaches 256 KiB -> triggers exactly one 256 KiB write at offset 0.
        GeneratedContent.Fill(seed, offsetCursor, buffer.AvailableWindow.Span[..chunkSize]);
        var firstFull = await buffer.AdvanceBufferedAsync(chunkSize, ct);
        firstFull.IsSuccess.Should().BeTrue();
        firstFull.FlushedBytes.Should().Be(PartFileWriter.WriteBufferSize);
        offsetCursor += chunkSize;
        writeOffsets.Should().Equal((0L, PartFileWriter.WriteBufferSize));

        // 3. Feed 2 chunks (32 KiB), advance clock by 499 ms -> still buffered; advance by 1 ms more -> age flush!
        GeneratedContent.Fill(seed, offsetCursor, buffer.AvailableWindow.Span[..chunkSize]);
        (await buffer.AdvanceBufferedAsync(chunkSize, ct)).FlushedBytes.Should().Be(0);
        offsetCursor += chunkSize;

        clock.Advance(TimeSpan.FromMilliseconds(499));
        GeneratedContent.Fill(seed, offsetCursor, buffer.AvailableWindow.Span[..chunkSize]);
        (await buffer.AdvanceBufferedAsync(chunkSize, ct)).FlushedBytes.Should().Be(0);
        offsetCursor += chunkSize;

        clock.Advance(TimeSpan.FromMilliseconds(1));
        GeneratedContent.Fill(seed, offsetCursor, buffer.AvailableWindow.Span[..chunkSize]);
        var ageFlush = await buffer.AdvanceBufferedAsync(chunkSize, ct);
        ageFlush.IsSuccess.Should().BeTrue();
        ageFlush.FlushedBytes.Should().Be(3 * chunkSize);
        offsetCursor += chunkSize;
        writeOffsets.Should().HaveCount(2);
        writeOffsets[1].Should().Be((PartFileWriter.WriteBufferSize, 3 * chunkSize));

        // 4. Feed remaining chunks to LeaseEnd -> automatically flushes final tail and releases the rented array.
        while (offsetCursor < totalSize)
        {
            var take = (int)Math.Min(chunkSize, buffer.AvailableWindow.Length);
            GeneratedContent.Fill(seed, offsetCursor, buffer.AvailableWindow.Span[..take]);
            var res = await buffer.AdvanceBufferedAsync(take, ct);
            res.IsSuccess.Should().BeTrue();
            offsetCursor += take;
        }

        buffer.HasActiveLease.Should().BeFalse();
        buffer.IsArrayRented.Should().BeFalse();

        (await writer.SealAsync(totalSize, totalSize, ct)).IsSuccess.Should().BeTrue();
        (await writer.CommitAsync(cancellationToken: ct)).IsSuccess.Should().BeTrue();

        var diskBytes = await File.ReadAllBytesAsync(writer.FinalPath, ct);
        Convert.ToHexStringLower(SHA256.HashData(diskBytes))
            .Should().Be(GeneratedContent.Sha256Hex(seed, totalSize));
    }

    [Fact]
    public async Task AvailableWindow_is_capped_at_LeaseEnd_and_DropSuperseded_discards_unflushed_bytes_and_returns_array()
    {
        var ct = TestContext.Current.CancellationToken;
        using var opened = PartFileWriter.TryOpen(_tempDir, "window-cap.bin", 1024 * 1024).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        var writes = 0;
        writer.WriteFaultInjector = (_, _) =>
        {
            writes++;
            return null;
        };

        using var buffer = new PooledPartWriteBuffer(writer);
        buffer.BeginLease(leaseStart: 100_000, leaseEnd: 150_000);

        buffer.AvailableWindow.Length.Should().Be(50_000);
        buffer.AvailableWindow.Span[..20_000].Fill(0x5A);
        (await buffer.AdvanceBufferedAsync(20_000, ct)).FlushedBytes.Should().Be(0);
        buffer.AvailableWindow.Length.Should().Be(30_000);

        // Superseded endgame lease drops the 20 KB without writing and returns the pooled array.
        buffer.DropSuperseded();

        buffer.HasActiveLease.Should().BeFalse();
        buffer.IsArrayRented.Should().BeFalse();
        buffer.BufferedBytes.Should().Be(0);
        writes.Should().Be(0);
    }

    [Fact]
    public async Task Write_error_reports_zero_flushed_bytes_and_keeps_buffered_data_for_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 64 * 1024;
        var expected = GeneratedContent.Bytes("retry-buf", size);

        using var opened = PartFileWriter.TryOpen(_tempDir, "retry-buf.bin", size).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;
        writer.WriteFaultInjector = (_, _) => new IOException("Disk full", unchecked((int)0x80070070));

        using var buffer = new PooledPartWriteBuffer(writer);
        buffer.BeginLease(0, size);
        expected.CopyTo(buffer.AvailableWindow.Span);

        // Reaching leaseEnd attempts to flush, which fails with NotEnoughSpace and FlushedBytes == 0.
        var failed = await buffer.AdvanceBufferedAsync(size, ct);
        failed.IsSuccess.Should().BeFalse();
        failed.Status.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);
        failed.FlushedBytes.Should().Be(0);
        buffer.HasActiveLease.Should().BeTrue();
        buffer.BufferedBytes.Should().Be(size);

        // Clear fault and complete the lease -> flushes the buffered 64 KiB cleanly.
        writer.WriteFaultInjector = null;
        var recovered = await buffer.CompleteLeaseAsync(ct);
        recovered.IsSuccess.Should().BeTrue();
        recovered.FlushedBytes.Should().Be(size);
        buffer.HasActiveLease.Should().BeFalse();
        buffer.IsArrayRented.Should().BeFalse();

        (await writer.SealAsync(size, size, ct)).IsSuccess.Should().BeTrue();
        (await writer.CommitAsync(cancellationToken: ct)).IsSuccess.Should().BeTrue();
        (await File.ReadAllBytesAsync(writer.FinalPath, ct)).Should().Equal(expected);
    }
}

[Collection(NonParallelAllocationGroup.Name)]
[Trait("Requirement", "KASI-ENG-008")]
public sealed class PooledPartWriteBufferAllocationTests : IDisposable
{
    private readonly string _tempDir;

    public PooledPartWriteBufferAllocationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kasi-alloc-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Writing_32_MiB_in_16_KiB_chunks_allocates_nothing_per_chunk_and_stays_within_bounded_overhead_per_256_KiB_write()
    {
        var ct = TestContext.Current.CancellationToken;
        const int chunkSize = 16 * 1024;
        const int totalBytes = 32 * 1024 * 1024;
        const int totalWrites = totalBytes / PartFileWriter.WriteBufferSize; // 128 writes of 256 KiB

        using var opened = PartFileWriter.TryOpen(_tempDir, "alloc-single.bin", totalBytes).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;
        using var buffer = new PooledPartWriteBuffer(writer, maxBufferAge: TimeSpan.FromHours(1));

        // Warm up the async write path and ArrayPool.
        buffer.BeginLease(0, PartFileWriter.WriteBufferSize);
        for (var i = 0; i < PartFileWriter.WriteBufferSize / chunkSize; i++)
        {
            buffer.AvailableWindow.Span[..chunkSize].Fill(0x11);
            var warmup = await buffer.AdvanceBufferedAsync(chunkSize, ct);
            if (!warmup.IsSuccess)
            {
                throw new InvalidOperationException("Warmup write failed.");
            }
        }

        // Measure 15 non-flushing 16 KiB chunks: must allocate 0 B.
        buffer.BeginLease(0, totalBytes);
        var beforeChunks = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 15; i++)
        {
            buffer.AvailableWindow.Span[..chunkSize].Fill(0x22);
            var step = await buffer.AdvanceBufferedAsync(chunkSize, ct);
            if (!step.IsSuccess || step.FlushedBytes != 0)
            {
                throw new InvalidOperationException("Unexpected flush during non-flushing chunk loop.");
            }
        }

        var chunkAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeChunks;
        chunkAllocated.Should().Be(0L, "buffering 16 KiB network chunks inside the rented 256 KiB array must not allocate");

        // Complete the remaining chunks across the full 32 MiB (2048 chunks / 128 OS writes).
        var beforeFullRun = GC.GetTotalAllocatedBytes(precise: true);
        while (buffer.HasActiveLease)
        {
            buffer.AvailableWindow.Span[..chunkSize].Fill(0x22);
            var step = await buffer.AdvanceBufferedAsync(chunkSize, ct);
            if (!step.IsSuccess)
            {
                throw new InvalidOperationException("Write failed during allocation measurement.");
            }
        }

        var fullRunAllocated = GC.GetTotalAllocatedBytes(precise: true) - beforeFullRun;
        var bytesPerWrite = (double)fullRunAllocated / totalWrites;
        bytesPerWrite.Should().BeLessThan(1024.0, "each 256 KiB RandomAccess.WriteAsync call may only allocate its small OS overlapped state");
    }

    [Fact]
    public async Task Eight_concurrent_writers_writing_32_MiB_in_16_KiB_chunks_stay_within_bounded_per_write_allocation()
    {
        var ct = TestContext.Current.CancellationToken;
        const int workers = 8;
        const int chunkSize = 16 * 1024;
        const int bytesPerWorker = 4 * 1024 * 1024;
        const int totalBytes = workers * bytesPerWorker; // 32 MiB
        const int totalWrites = totalBytes / PartFileWriter.WriteBufferSize; // 128 writes

        using var opened = PartFileWriter.TryOpen(_tempDir, "alloc-concurrent.bin", totalBytes).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;
        var buffers = Enumerable.Range(0, workers)
            .Select(_ => new PooledPartWriteBuffer(writer, maxBufferAge: TimeSpan.FromHours(1)))
            .ToArray();

        try
        {
            // Hold all 8 leases simultaneously during warmup so ArrayPool has all 8 x 256 KiB arrays allocated before measurement.
            for (var w = 0; w < workers; w++)
            {
                var start = (long)w * PartFileWriter.WriteBufferSize;
                buffers[w].BeginLease(start, start + PartFileWriter.WriteBufferSize);
                buffers[w].AvailableWindow.Span[..PartFileWriter.WriteBufferSize].Fill(0x33);
            }

            for (var w = 0; w < workers; w++)
            {
                var warmup = await buffers[w].AdvanceBufferedAsync(PartFileWriter.WriteBufferSize, ct);
                if (!warmup.IsSuccess)
                {
                    throw new InvalidOperationException("Warmup write failed.");
                }
            }

            // Pre-begin all 8 leases so they hold their pooled 256 KiB arrays across the 4 MiB transfer.
            for (var w = 0; w < workers; w++)
            {
                var start = (long)w * bytesPerWorker;
                buffers[w].BeginLease(start, start + bytesPerWorker);
            }

            var tasks = new Task[workers];
            var before = GC.GetTotalAllocatedBytes(precise: true);

            for (var w = 0; w < workers; w++)
            {
                var workerIndex = w;
                tasks[w] = Task.Run(
                    async () =>
                    {
                        var buf = buffers[workerIndex];
                        while (buf.HasActiveLease)
                        {
                            buf.AvailableWindow.Span[..chunkSize].Fill((byte)workerIndex);
                            var res = await buf.AdvanceBufferedAsync(chunkSize, ct);
                            if (!res.IsSuccess)
                            {
                                throw new InvalidOperationException("Concurrent write failed.");
                            }
                        }
                    },
                    ct);
            }

            await Task.WhenAll(tasks);

            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
            var bytesPerWrite = (double)allocated / totalWrites;
            bytesPerWrite.Should().BeLessThan(1024.0, "concurrent 256 KiB writes must not allocate per 16 KiB chunk or leak buffers");
        }
        finally
        {
            foreach (var buf in buffers)
            {
                buf.Dispose();
            }
        }
    }
}
