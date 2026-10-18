using System.Buffers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-001")]
[Trait("Requirement", "KASI-ENG-008")]
public sealed class HttpTransferIntegrationTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        finally
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-001")]
    public async Task Segmented_download_writes_one_part_file_then_renames_and_matches_SHA256()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-segmented";
        const int size = 4 * 1024 * 1024;
        var runner = new HttpTransferRunner();

        var outcome = await runner.DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(initialConnections: 4),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.TotalBytes.Should().Be(size);
        completed.RangesSupported.Should().BeTrue();
        completed.Resumable.Should().BeTrue();
        completed.FinalPath.Should().Be(Path.Combine(_folder, size.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        File.Exists(completed.FinalPath).Should().BeTrue();
        File.Exists(completed.FinalPath + PartFileWriter.PartExtension).Should().BeFalse();
        completed.FinalSegments.Should().OnlyContain(s => s.IsComplete);
        completed.FinalSegments.Sum(s => s.Length).Should().Be(size);
        var completedBytes = await File.ReadAllBytesAsync(completed.FinalPath, ct);
        Convert.ToHexStringLower(SHA256.HashData(completedBytes)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
        _host.Metrics.AbortedRequests.Should().Be(0);
    }

    [Fact]
    [Trait("TC", "TC-ENG-012")]
    public async Task A_zero_byte_file_completes_without_a_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url("/files/runner-zero/0"),
            _folder,
            new HttpTransferOptions(),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.TotalBytes.Should().Be(0);
        completed.FinalSegments.Should().BeEmpty();
        (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().BeEmpty();
    }

    [Fact]
    [Trait("TC", "TC-ENG-012")]
    public async Task Range_less_server_falls_back_to_one_stream_and_is_not_resumable()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-norange";
        const int size = 2 * 1024 * 1024;

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/norange/{seed}/{size}"),
            _folder,
            new HttpTransferOptions(),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.RangesSupported.Should().BeFalse();
        completed.Resumable.Should().BeFalse();
        (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(seed, size));
        _host.Metrics.TotalConnectionsAccepted.Should().Be(1);
    }

    [Fact]
    [Trait("TC", "TC-ENG-013")]
    public async Task Chunked_server_without_a_length_completes_and_reports_the_bytes_received()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-chunked";
        const int size = 2 * 1024 * 1024;

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/chunked/{seed}/{size}"),
            _folder,
            new HttpTransferOptions(),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.RangesSupported.Should().BeFalse();
        completed.Resumable.Should().BeFalse();
        completed.TotalBytes.Should().Be(size);
        (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(seed, size));
        _host.Metrics.TotalConnectionsAccepted.Should().Be(1);
    }

    [Fact]
    [Trait("TC", "TC-ENG-001")]
    public async Task One_GiB_over_HTTP1_with_eight_connections_matches_SHA256()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-1gib-http1";
        const long size = 1L << 30;

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(initialConnections: 8),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.TotalBytes.Should().Be(size);
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
        _host.Metrics.PeakConcurrentConnections.Should().Be(8);
        _host.Metrics.TotalConnectionsAccepted.Should().Be(8);
        _host.Metrics.AbortedRequests.Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [Trait("TC", "TC-ENG-006")]
    public async Task The_configured_connection_count_is_reached_and_the_file_matches(int connections)
    {
        var ct = TestContext.Current.CancellationToken;
        var seed = $"runner-count-{connections}";
        const long size = 256L * 1024 * 1024;

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(initialConnections: connections),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
        _host.Metrics.TotalConnectionsAccepted.Should().Be(connections);
        _host.Metrics.AbortedRequests.Should().Be(0);
    }

    [Fact]
    [Trait("TC", "TC-ENG-002")]
    [Trait("TC", "TC-ENG-010")]
    public async Task HTTPS_download_uses_HTTP2_and_never_aborts_a_lease()
    {
        await _host.DisposeAsync();
        _host = await TestServerHost.StartAsync(
            new TestServerOptions
            {
                TlsMode = TestServerTlsMode.Valid,
                ProtocolMode = TestServerProtocolMode.Http2,
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-http2";
        const long size = 1L << 30;
        var runner = new HttpTransferRunner(new HttpHostPoolOptions
        {
            CustomTrustRoots = new X509Certificate2Collection(_host.RootCertificate!),
        });

        var outcome = await runner.DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(initialConnections: 8),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
        _host.Metrics.Http1RequestsServed.Should().Be(0);
        _host.Metrics.Http2RequestsServed.Should().BeGreaterThan(0);
        _host.Metrics.PeakConcurrentConnections.Should().BeGreaterThan(1);
        _host.Metrics.AbortedRequests.Should().Be(0);
        _host.Metrics.TlsHandshakesCompleted.Should().BeLessThanOrEqualTo(10);
    }

    [Fact]
    [Trait("TC", "TC-RES-002")]
    public async Task Cancelling_mid_download_returns_Cancelled_and_resume_completes_from_the_saved_cursors()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-resume";
        const int size = 16 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 256 * 1024 });
        var options = new HttpTransferOptions
        {
            SchedulerOptions = new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4 },
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = new HttpTransferRunner().DownloadAsync(Url($"/files/{seed}/{size}"), _folder, options, cts.Token);
        await EventuallyAsync(() => _host.Metrics.Http1RequestsServed >= 2);
        await Task.Delay(900, ct);
        await cts.CancelAsync();

        var failed = (await download).Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
        failed.SegmentsSnapshot.Sum(s => s.Written).Should().BeGreaterThan(0);
        failed.ResolvedFileName.Should().NotBeNull();
        failed.TotalBytes.Should().Be(size);
        failed.ETag.Should().NotBeNull();
        var partPath = Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension);
        File.Exists(partPath).Should().BeTrue("the cancellation path preserves the part file");

        var resumed = await new HttpTransferRunner().ResumeAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            failed.ResolvedFileName!,
            size,
            failed.ETag,
            failed.LastModified,
            failed.SegmentsSnapshot,
            new HttpTransferOptions
            {
                SchedulerOptions = new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4 },
            },
            ct);

        var completed = resumed.Should().BeOfType<TransferCompleted>().Subject;
        File.Exists(completed.FinalPath).Should().BeTrue();
        File.Exists(partPath).Should().BeFalse();
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    [Trait("TC", "TC-RES-001")]
    public async Task A_failed_flush_on_the_stop_path_offers_no_snapshot_because_nothing_new_is_known_to_be_durable()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-flush-fails";
        const int size = 16 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 256 * 1024 });
        var options = new HttpTransferOptions
        {
            SchedulerOptions = new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4 },
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
            FlushFaultInjector = () => new IOException("simulated disk full", unchecked((int)0x80070070)),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = new HttpTransferRunner().DownloadAsync(Url($"/files/{seed}/{size}"), _folder, options, cts.Token);
        await EventuallyAsync(() => _host.Metrics.Http1RequestsServed >= 2);
        await Task.Delay(900, ct);
        await cts.CancelAsync();

        var failed = (await download).Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.DiskNotEnoughSpace, "the failed flush, not the cancellation, is what the caller must act on");
        failed.SegmentsSnapshot.Should().BeEmpty("a snapshot is only recordable after a successful flush (ADR 0003)");
        failed.BytesFlushed.Should().Be(0);
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension)).Should().BeTrue("the part file is still preserved");
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task Insufficient_space_at_open_preserves_the_destination_and_writes_no_part_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url("/files/runner-no-space/65536"),
            _folder,
            new HttpTransferOptions { SpaceProvider = new FixedSpaceProvider(0) },
            ct);

        outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.DiskNotEnoughSpace);
        Directory.GetFiles(_folder).Should().BeEmpty();
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task Mid_transfer_disk_full_preserves_the_part_file_and_resume_completes_after_space_is_freed()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-disk-full";
        const int size = 8 * 1024 * 1024;
        var injected = 0;
        var options = Options(initialConnections: 4) with
        {
            WriteFaultInjector = (offset, _) =>
                offset >= 256 * 1024 && Interlocked.Exchange(ref injected, 1) == 0
                    ? new IOException("simulated disk full", unchecked((int)0x80070070))
                    : null,
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            options,
            ct);

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.DiskNotEnoughSpace);
        failed.BytesFlushed.Should().BeGreaterThan(0);
        failed.SegmentsSnapshot.Sum(s => s.Written).Should().Be(failed.BytesFlushed);
        failed.ResolvedFileName.Should().NotBeNull();
        var partPath = Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension);
        File.Exists(partPath).Should().BeTrue();
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName!)).Should().BeFalse();

        var resumed = await new HttpTransferRunner().ResumeAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            failed.ResolvedFileName!,
            size,
            failed.ETag,
            failed.LastModified,
            failed.SegmentsSnapshot,
            Options(initialConnections: 4),
            ct);

        var completed = resumed.Should().BeOfType<TransferCompleted>().Subject;
        File.Exists(partPath).Should().BeFalse();
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    [Trait("TC", "TC-RES-003")]
    public async Task Resume_after_the_remote_file_changed_returns_FileChanged_and_leaves_the_part_file_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        const string id = "runner-mutable";
        const int size = 8 * 1024 * 1024;
        _host.Mutable.Change(id, size);
        var chaosId = _host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 256 * 1024 });
        var options = new HttpTransferOptions
        {
            SchedulerOptions = new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4 },
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = new HttpTransferRunner().DownloadAsync(Url($"/mutable/{id}"), _folder, options, cts.Token);
        await EventuallyAsync(() => _host.Metrics.Http1RequestsServed >= 2);
        await Task.Delay(900, ct);
        await cts.CancelAsync();

        var failed = (await download).Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
        failed.ETag.Should().NotBeNull();
        var partPath = Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension);
        File.Exists(partPath).Should().BeTrue();
        var partLength = new FileInfo(partPath).Length;

        _host.Mutable.Change(id);

        var resumed = await new HttpTransferRunner().ResumeAsync(
            Url($"/mutable/{id}"),
            _folder,
            failed.ResolvedFileName!,
            size,
            failed.ETag,
            failed.LastModified,
            failed.SegmentsSnapshot,
            Options(initialConnections: 4),
            ct);

        resumed.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.FileChanged);
        File.Exists(partPath).Should().BeTrue("a changed file must not destroy the resumable part");
        new FileInfo(partPath).Length.Should().Be(partLength);
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName!)).Should().BeFalse();
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task A_truncated_lease_fails_with_ProtocolError_and_preserves_the_part_file()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-truncated";
        const int size = 8 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { TruncateAfterBytes = 128 * 1024 });
        var options = Options(initialConnections: 4) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            options,
            ct);

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.ProtocolError);
        failed.ResolvedFileName.Should().NotBeNull();
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName! + PartFileWriter.PartExtension)).Should().BeTrue();
        File.Exists(Path.Combine(_folder, failed.ResolvedFileName!)).Should().BeFalse();
    }

    [Fact]
    public async Task OverwriteDestination_replaces_an_existing_file_with_the_verified_download()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "runner-overwrite";
        const int size = 65536;
        var destination = Path.Combine(_folder, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await File.WriteAllTextAsync(destination, "old", ct);

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions { OverwriteDestination = true },
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(seed, size));
        File.Exists(destination + PartFileWriter.PartExtension).Should().BeFalse();
    }

    [Fact]
    public async Task Existing_destination_fails_fast_with_TargetExists_and_leaves_it_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = Path.Combine(_folder, "64");
        await File.WriteAllTextAsync(destination, "keep", ct);

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url("/files/runner-target/64"),
            _folder,
            new HttpTransferOptions(),
            ct);

        outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.DiskTargetExists);
        (await File.ReadAllTextAsync(destination, ct)).Should().Be("keep");
        File.Exists(destination + PartFileWriter.PartExtension).Should().BeFalse();
    }

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private static HttpTransferOptions Options(int initialConnections) => new()
    {
        SchedulerOptions = new SegmentSchedulerOptions
        {
            MaxConnections = initialConnections,
            InitialConnections = initialConnections,
        },
    };

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hasher.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the server should see the first lease requests");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FixedSpaceProvider(long availableBytes) : IDiskSpaceProvider
    {
        public bool TryGetAvailableFreeBytes(string directoryPath, out long available)
        {
            available = availableBytes;
            return true;
        }
    }
}
