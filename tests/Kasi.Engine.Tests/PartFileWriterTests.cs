using System.Security.Cryptography;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

[Collection(NonParallelAllocationGroup.Name)]
[Trait("Requirement", "KASI-ENG-008")]
[Trait("Requirement", "KASI-RES-001")]
public sealed class PartFileWriterTests : IDisposable
{
    private readonly string _tempDir;

    public PartFileWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kasi-part-tests-{Guid.NewGuid():N}");
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
            // Best-effort cleanup of temp folder.
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-014")]
    public async Task Opens_sparse_kasipart_writes_out_of_order_and_overlapping_ranges_seals_clears_sparse_and_commits_matching_SHA256()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "tc-eng-014";
        const int size = 10 * 1024 * 1024;
        const string fileName = "ubuntu.iso";

        using var opened = PartFileWriter.TryOpen(_tempDir, fileName, size).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        File.Exists(writer.PartPath).Should().BeTrue();
        File.Exists(writer.FinalPath).Should().BeFalse();
        new FileInfo(writer.PartPath).Length.Should().Be(size);

        if (OperatingSystem.IsWindows())
        {
            writer.IsSparse.Should().BeTrue();
            (File.GetAttributes(writer.PartPath) & FileAttributes.SparseFile).Should().Be(FileAttributes.SparseFile);
        }

        const int chunkSize = PartFileWriter.WriteBufferSize;
        var chunkIndices = Enumerable.Range(0, size / chunkSize).Reverse().ToArray();

        await Parallel.ForEachAsync(
            chunkIndices,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (chunkIndex, token) =>
            {
                var offset = (long)chunkIndex * chunkSize;
                var buffer = new byte[chunkSize];
                GeneratedContent.Fill(seed, offset, buffer);
                var status = await writer.WriteAsync(buffer, offset, token);
                status.IsSuccess.Should().BeTrue();
            });

        // Simulate an endgame duplicate write overlapping the last 512 KiB.
        var endgameOffset = size - (2 * chunkSize);
        var endgameBytes = new byte[2 * chunkSize];
        GeneratedContent.Fill(seed, endgameOffset, endgameBytes);
        (await writer.WriteAsync(endgameBytes, endgameOffset, ct)).IsSuccess.Should().BeTrue();

        var sealStatus = await writer.SealAsync(expectedLength: size, bytesWritten: size, ct);
        sealStatus.IsSuccess.Should().BeTrue();
        writer.IsSealed.Should().BeTrue();

        if (OperatingSystem.IsWindows())
        {
            writer.IsSparse.Should().BeFalse();
            (File.GetAttributes(writer.PartPath) & FileAttributes.SparseFile).Should().Be(0);
        }

        // Handle remains open and readable while sealed (for M1.12 checksum / M2.11 Mark-of-the-Web).
        File.Exists(writer.PartPath).Should().BeTrue();
        File.Exists(writer.FinalPath).Should().BeFalse();

        var commitStatus = await writer.CommitAsync(overwriteDestination: false, ct);
        commitStatus.IsSuccess.Should().BeTrue();
        writer.IsCommitted.Should().BeTrue();

        File.Exists(writer.PartPath).Should().BeFalse();
        File.Exists(writer.FinalPath).Should().BeTrue();
        new FileInfo(writer.FinalPath).Length.Should().Be(size);

        if (OperatingSystem.IsWindows())
        {
            (File.GetAttributes(writer.FinalPath) & FileAttributes.SparseFile).Should().Be(0);
        }

        var actualBytes = await File.ReadAllBytesAsync(writer.FinalPath, ct);
        Convert.ToHexStringLower(SHA256.HashData(actualBytes))
            .Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task Explicitly_written_all_zero_blocks_remain_allocated_on_sparse_NTFS_and_pass_SealAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 512 * 1024;
        using var opened = PartFileWriter.TryOpen(_tempDir, "zeros.bin", size).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        var zeros = new byte[PartFileWriter.WriteBufferSize];
        (await writer.WriteAsync(zeros, 0, ct)).IsSuccess.Should().BeTrue();
        (await writer.WriteAsync(zeros, PartFileWriter.WriteBufferSize, ct)).IsSuccess.Should().BeTrue();

        var sealStatus = await writer.SealAsync(expectedLength: size, bytesWritten: size, ct);
        sealStatus.IsSuccess.Should().BeTrue();

        if (OperatingSystem.IsWindows())
        {
            (File.GetAttributes(writer.PartPath) & FileAttributes.SparseFile).Should().Be(0);
        }

        (await writer.CommitAsync(cancellationToken: ct)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task SealAsync_rejects_file_with_unwritten_sparse_hole_even_when_caller_passes_matching_bytesWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 3 * 256 * 1024;
        using var opened = PartFileWriter.TryOpen(_tempDir, "hole.bin", size).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        var block = new byte[256 * 1024];
        block.AsSpan().Fill(0xAB);

        // Write first 256 KiB and last 256 KiB, leaving the middle 256 KiB as an unallocated sparse hole.
        (await writer.WriteAsync(block, 0, ct)).IsSuccess.Should().BeTrue();
        (await writer.WriteAsync(block, 2 * 256 * 1024, ct)).IsSuccess.Should().BeTrue();

        var sealStatus = await writer.SealAsync(expectedLength: size, bytesWritten: size, ct);

        sealStatus.IsSuccess.Should().BeFalse();
        sealStatus.ErrorKind.Should().Be(DiskErrorKind.IncompleteData);
        writer.IsSealed.Should().BeFalse();
        File.Exists(writer.PartPath).Should().BeTrue();
        File.Exists(writer.FinalPath).Should().BeFalse();
    }

    [Fact]
    public async Task SealAsync_rejects_when_bytesWritten_is_less_than_file_length()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 256 * 1024;
        using var opened = PartFileWriter.TryOpen(_tempDir, "short-count.bin", size).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        var block = new byte[size];
        (await writer.WriteAsync(block, 0, ct)).IsSuccess.Should().BeTrue();

        var sealStatus = await writer.SealAsync(expectedLength: size, bytesWritten: size - 1024, ct);

        sealStatus.IsSuccess.Should().BeFalse();
        sealStatus.ErrorKind.Should().Be(DiskErrorKind.IncompleteData);
    }

    [Fact]
    public async Task SealAsync_rejects_when_actual_length_differs_from_expectedLength()
    {
        var ct = TestContext.Current.CancellationToken;
        using var opened = PartFileWriter.TryOpen(_tempDir, "chunked-mismatch.bin", totalBytes: null)
            .Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        var data = new byte[4096];
        (await writer.WriteAsync(data, 0, ct)).IsSuccess.Should().BeTrue();

        var sealStatus = await writer.SealAsync(expectedLength: 8192, bytesWritten: 4096, ct);

        sealStatus.IsSuccess.Should().BeFalse();
        sealStatus.ErrorKind.Should().Be(DiskErrorKind.LengthMismatch);
        File.Exists(writer.PartPath).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_returns_TargetExists_when_destination_exists_and_replaces_when_overwrite_is_true()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 4096;
        var existingPath = Path.Combine(_tempDir, "existing.bin");
        await File.WriteAllBytesAsync(existingPath, [1, 2, 3], ct);

        using var opened = PartFileWriter.TryOpen(_tempDir, "existing.bin", size).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        var payload = GeneratedContent.Bytes("new-file", size);
        (await writer.WriteAsync(payload, 0, ct)).IsSuccess.Should().BeTrue();
        (await writer.SealAsync(size, size, ct)).IsSuccess.Should().BeTrue();

        var noOverwrite = await writer.CommitAsync(overwriteDestination: false, ct);
        noOverwrite.IsSuccess.Should().BeFalse();
        noOverwrite.ErrorKind.Should().Be(DiskErrorKind.TargetExists);
        File.Exists(writer.PartPath).Should().BeTrue();
        (await File.ReadAllBytesAsync(existingPath, ct)).Should().Equal(1, 2, 3);

        var withOverwrite = await writer.CommitAsync(overwriteDestination: true, ct);
        withOverwrite.IsSuccess.Should().BeTrue();
        File.Exists(writer.PartPath).Should().BeFalse();
        (await File.ReadAllBytesAsync(existingPath, ct)).Should().Equal(payload);
    }

    [Fact]
    public void TryOpen_pre_start_check_returns_NotEnoughSpace_when_fresh_download_exceeds_available_bytes()
    {
        var stubSpace = new StubDiskSpaceProvider(availableBytes: 50L * 1024 * 1024);

        var outcome = PartFileWriter.TryOpen(_tempDir, "large.bin", totalBytes: 100L * 1024 * 1024, spaceProvider: stubSpace);

        var failure = outcome.Should().BeOfType<PartFileOpenFailure>().Subject;
        failure.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);
        File.Exists(Path.Combine(_tempDir, "large.bin.kasipart")).Should().BeFalse();
    }

    [Fact]
    public async Task TryOpen_on_resume_requires_only_remaining_unwritten_bytes_and_preserves_written_data()
    {
        var ct = TestContext.Current.CancellationToken;
        const int totalBytes = 1024 * 1024;
        const int halfBytes = 512 * 1024;
        var allBytes = GeneratedContent.Bytes("resume-seed", totalBytes);

        using (var initial = PartFileWriter.TryOpen(_tempDir, "resume.bin", totalBytes).Should().BeOfType<PartFileOpened>().Subject)
        {
            (await initial.Writer.WriteAsync(allBytes.AsMemory(0, halfBytes), 0, ct)).IsSuccess.Should().BeTrue();
            (await initial.Writer.FlushAsync(ct)).IsSuccess.Should().BeTrue();
        }

        // Only 600 KiB free on disk, which is less than 1 MiB totalBytes but more than the remaining 512 KiB.
        var stubSpace = new StubDiskSpaceProvider(availableBytes: 600 * 1024);
        using var resumed = PartFileWriter.TryOpen(
            _tempDir,
            "resume.bin",
            totalBytes,
            resume: true,
            alreadyWrittenBytes: halfBytes,
            spaceProvider: stubSpace).Should().BeOfType<PartFileOpened>().Subject;

        (await resumed.Writer.WriteAsync(allBytes.AsMemory(halfBytes, halfBytes), halfBytes, ct)).IsSuccess.Should().BeTrue();
        (await resumed.Writer.SealAsync(totalBytes, totalBytes, ct)).IsSuccess.Should().BeTrue();
        (await resumed.Writer.CommitAsync(cancellationToken: ct)).IsSuccess.Should().BeTrue();

        (await File.ReadAllBytesAsync(resumed.Writer.FinalPath, ct)).Should().Equal(allBytes);
    }

    [Fact]
    public async Task TryOpen_on_resume_fails_with_PartFileMismatch_when_part_file_is_missing_or_wrong_length()
    {
        var ct = TestContext.Current.CancellationToken;

        var missingOutcome = PartFileWriter.TryOpen(_tempDir, "missing.bin", 4096, resume: true);
        missingOutcome.Should().BeOfType<PartFileOpenFailure>().Subject.ErrorKind.Should().Be(DiskErrorKind.PartFileMismatch);

        var wrongLengthPart = Path.Combine(_tempDir, "wrong.bin.kasipart");
        await File.WriteAllBytesAsync(wrongLengthPart, new byte[1024], ct);

        var wrongOutcome = PartFileWriter.TryOpen(_tempDir, "wrong.bin", 4096, resume: true);
        wrongOutcome.Should().BeOfType<PartFileOpenFailure>().Subject.ErrorKind.Should().Be(DiskErrorKind.PartFileMismatch);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070070))]
    [InlineData(unchecked((int)0x80070027))]
    [InlineData(unchecked((int)0x8007050F))]
    public async Task Classifies_disk_full_and_quota_IOExceptions_during_SetLength_Write_and_Flush_as_NotEnoughSpace(int hResult)
    {
        var ct = TestContext.Current.CancellationToken;

        // 1. SetLength failure cleans up the empty .kasipart and returns NotEnoughSpace.
        var openFailure = PartFileWriter.TryOpenCore(
            _tempDir,
            "setlength-fail.bin",
            4096,
            resume: false,
            alreadyWrittenBytes: 0,
            spaceProvider: null,
            setLengthFaultInjector: () => new IOException("Disk full on SetLength", hResult));

        openFailure.Should().BeOfType<PartFileOpenFailure>().Subject.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);
        File.Exists(Path.Combine(_tempDir, "setlength-fail.bin.kasipart")).Should().BeFalse();

        // 2. WriteAsync failure returns NotEnoughSpace without throwing; rewriting on resume produces the intact file.
        const int size = 8192;
        var expected = GeneratedContent.Bytes("write-retry", size);
        using var opened = PartFileWriter.TryOpen(_tempDir, "write-fail.bin", size).Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        (await writer.WriteAsync(expected.AsMemory(0, 4096), 0, ct)).IsSuccess.Should().BeTrue();
        writer.WriteFaultInjector = (_, _) => new IOException("Disk full on WriteAsync", hResult);

        var failedWrite = await writer.WriteAsync(expected.AsMemory(4096, 4096), 4096, ct);
        failedWrite.IsSuccess.Should().BeFalse();
        failedWrite.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);

        // 3. FlushAsync failure returns NotEnoughSpace without throwing.
        writer.FlushFaultInjector = () => new IOException("Disk full on FlushAsync", hResult);
        var failedFlush = await writer.FlushAsync(ct);
        failedFlush.IsSuccess.Should().BeFalse();
        failedFlush.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);

        // Clear fault injectors, rewrite the failed range, seal and commit.
        writer.WriteFaultInjector = null;
        writer.FlushFaultInjector = null;
        (await writer.WriteAsync(expected.AsMemory(4096, 4096), 4096, ct)).IsSuccess.Should().BeTrue();
        (await writer.SealAsync(size, size, ct)).IsSuccess.Should().BeTrue();
        (await writer.CommitAsync(cancellationToken: ct)).IsSuccess.Should().BeTrue();

        (await File.ReadAllBytesAsync(writer.FinalPath, ct)).Should().Equal(expected);
    }

    [Fact]
    public async Task Unknown_length_and_zero_byte_files_seal_and_commit_cleanly()
    {
        var ct = TestContext.Current.CancellationToken;

        // Unknown length (chunked)
        var chunkedBytes = GeneratedContent.Bytes("chunked-disk", 70_000);
        using (var chunked = PartFileWriter.TryOpen(_tempDir, "chunked.bin", totalBytes: null).Should().BeOfType<PartFileOpened>().Subject)
        {
            chunked.Writer.IsSparse.Should().BeFalse();
            (await chunked.Writer.WriteAsync(chunkedBytes.AsMemory(0, 30_000), 0, ct)).IsSuccess.Should().BeTrue();
            (await chunked.Writer.WriteAsync(chunkedBytes.AsMemory(30_000, 40_000), 30_000, ct)).IsSuccess.Should().BeTrue();
            (await chunked.Writer.SealAsync(expectedLength: 70_000, bytesWritten: 70_000, ct)).IsSuccess.Should().BeTrue();
            (await chunked.Writer.CommitAsync(cancellationToken: ct)).IsSuccess.Should().BeTrue();
            (await File.ReadAllBytesAsync(chunked.Writer.FinalPath, ct)).Should().Equal(chunkedBytes);
        }

        // Zero-byte file
        using (var zero = PartFileWriter.TryOpen(_tempDir, "zero.bin", totalBytes: 0).Should().BeOfType<PartFileOpened>().Subject)
        {
            (await zero.Writer.SealAsync(expectedLength: 0, bytesWritten: 0, ct)).IsSuccess.Should().BeTrue();
            (await zero.Writer.CommitAsync(cancellationToken: ct)).IsSuccess.Should().BeTrue();
            new FileInfo(zero.Writer.FinalPath).Length.Should().Be(0);
        }
    }

    [Fact]
    public void Abandon_closes_handle_and_deletes_part_file_whereas_Dispose_keeps_it()
    {
        using (var disposedOnly = PartFileWriter.TryOpen(_tempDir, "keep.bin", 4096).Should().BeOfType<PartFileOpened>().Subject)
        {
            File.Exists(disposedOnly.Writer.PartPath).Should().BeTrue();
        }

        File.Exists(Path.Combine(_tempDir, "keep.bin.kasipart")).Should().BeTrue();

        var abandoned = PartFileWriter.TryOpen(_tempDir, "abandon.bin", 4096).Should().BeOfType<PartFileOpened>().Subject.Writer;
        var partPath = abandoned.PartPath;
        File.Exists(partPath).Should().BeTrue();

        abandoned.Abandon();

        File.Exists(partPath).Should().BeFalse();
    }

    [Fact]
    public void Sharing_violation_when_second_writer_opens_same_part_file_returns_IoError()
    {
        using var first = PartFileWriter.TryOpen(_tempDir, "shared.bin", 4096).Should().BeOfType<PartFileOpened>().Subject;

        var second = PartFileWriter.TryOpen(_tempDir, "shared.bin", 4096);

        second.Should().BeOfType<PartFileOpenFailure>().Subject.ErrorKind.Should().Be(DiskErrorKind.IoError);
    }

    [Fact]
    [Trait("Requirement", "KASI-RES-001")]
    public async Task FlushAsync_while_concurrent_writes_are_in_flight_makes_all_pre_snapshot_cursors_durable_on_reopen()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "flush-in-flight";
        const int lanes = 4;
        const int blocksPerLane = 16;
        const int blockSize = 64 * 1024;
        const int laneLength = blocksPerLane * blockSize;
        const int totalSize = lanes * laneLength;

        var cursors = new long[lanes];
        for (var i = 0; i < lanes; i++)
        {
            cursors[i] = (long)i * laneLength;
        }

        long[]? snapshottedCursors = null;

        using (var opened = PartFileWriter.TryOpen(_tempDir, "concurrent-flush.bin", totalSize).Should().BeOfType<PartFileOpened>().Subject)
        {
            var writer = opened.Writer;
            var halfwayReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var completedBlocks = 0;

            var workerTasks = Enumerable.Range(0, lanes).Select(async lane =>
            {
                var laneStart = (long)lane * laneLength;
                var buffer = new byte[blockSize];
                for (var b = 0; b < blocksPerLane; b++)
                {
                    var offset = laneStart + ((long)b * blockSize);
                    GeneratedContent.Fill(seed, offset, buffer);
                    var status = await writer.WriteAsync(buffer, offset, ct);
                    status.IsSuccess.Should().BeTrue();

                    Volatile.Write(ref cursors[lane], offset + blockSize);
                    if (Interlocked.Increment(ref completedBlocks) == (lanes * blocksPerLane) / 2)
                    {
                        halfwayReached.TrySetResult();
                    }
                }
            }).ToArray();

            await halfwayReached.Task;

            // ADR 0003 / 0004 protocol: snapshot cursors BEFORE FlushAsync, then record them AFTER FlushAsync returns.
            snapshottedCursors = new long[lanes];
            for (var i = 0; i < lanes; i++)
            {
                snapshottedCursors[i] = Volatile.Read(ref cursors[i]);
            }

            var flushStatus = await writer.FlushAsync(ct);
            flushStatus.IsSuccess.Should().BeTrue();

            await Task.WhenAll(workerTasks);
        }

        // Reopen the .kasipart file and verify every byte below each snapshotted cursor matches GeneratedContent.
        var diskBytes = await File.ReadAllBytesAsync(Path.Combine(_tempDir, "concurrent-flush.bin.kasipart"), ct);
        for (var lane = 0; lane < lanes; lane++)
        {
            var laneStart = lane * laneLength;
            var recordedEnd = (int)snapshottedCursors![lane];
            var expectedSlice = new byte[recordedEnd - laneStart];
            GeneratedContent.Fill(seed, laneStart, expectedSlice);
            diskBytes.AsSpan(laneStart, recordedEnd - laneStart).ToArray().Should().Equal(expectedSlice);
        }
    }

    private sealed class StubDiskSpaceProvider(long availableBytes) : IDiskSpaceProvider
    {
        public bool TryGetAvailableFreeBytes(string directoryPath, out long available)
        {
            available = availableBytes;
            return true;
        }
    }
}
