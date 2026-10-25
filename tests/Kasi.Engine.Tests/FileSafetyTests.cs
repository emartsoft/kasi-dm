using System.Collections.Concurrent;
using Kasi.Core;

namespace Kasi.Engine.Tests;

/// <summary>
/// The step every finished download goes through, whatever made it (KASI-CAT-003, KASI-CAT-004; docs/plans/m2-11.md): HTTP, FTP and a media
/// mux all call <see cref="FileSafety.ApplyAsync"/>, so these tests cover the step once and each caller's wiring.
/// </summary>
[Trait("Requirement", "KASI-CAT-003")]
[Trait("TC", "TC-CAT-003")]
public sealed class FileSafetyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-safety-" + Guid.NewGuid().ToString("N"));

    public FileSafetyTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class Scanner(ScanResult answer, bool enabled = true) : IFileScanner
    {
        public int Calls { get; private set; }

        public bool IsEnabled { get; } = enabled;

        public Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer);
        }
    }

    // serves the bytes of an FTP file the way FtpLeaseTransport does: one lease, one stream
    private sealed class MemoryTransport(byte[] data) : ILeaseTransport
    {
        public Task<LeaseBody> OpenAsync(int workerId, LeaseAssignment lease, CancellationToken cancellationToken) =>
            Task.FromResult(new LeaseBody(new MemoryStream(data, (int)lease.Start, (int)lease.Length, writable: false), new MemoryStream()));
    }

    private string NewFile(string name = "a.bin")
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllText(path, "data");
        return path;
    }

    [Fact]
    public async Task A_flagged_file_is_not_marked_and_the_caller_is_told()
    {
        var path = NewFile();
        var log = new List<string>();

        var flagged = await FileSafety.ApplyAsync(path, new Uri("https://example.org/a"), new Scanner(ScanResult.Detected), log.Add, TestContext.Current.CancellationToken);

        flagged.Should().BeTrue();
        log.Should().Equal("Scan: flagged by antivirus");
        File.Exists(path + ":Zone.Identifier").Should().BeFalse("a file that is going to be failed is left exactly as the antivirus found it");
        File.Exists(path).Should().BeTrue("Kasi never deletes it");
    }

    [Fact]
    public async Task A_clean_or_unscanned_file_is_marked_and_a_scanner_that_is_off_is_never_asked()
    {
        var path = NewFile();
        var clean = new Scanner(ScanResult.Clean);
        var off = new Scanner(ScanResult.Detected, enabled: false);
        var log = new List<string>();

        (await FileSafety.ApplyAsync(path, null, clean, log.Add, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await FileSafety.ApplyAsync(path, null, off, log.Add, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await FileSafety.ApplyAsync(path, null, null, log.Add, TestContext.Current.CancellationToken)).Should().BeFalse();

        log.Should().Equal("Scan: clean");
        off.Calls.Should().Be(0);
        (await File.ReadAllTextAsync(path + ":Zone.Identifier", TestContext.Current.CancellationToken)).Should().Contain("ZoneId=3");
    }

    [Fact]
    public async Task A_volume_that_cannot_hold_the_mark_gets_one_fixed_log_line_and_the_file_still_passes()
    {
        var log = new List<string>();

        var flagged = await FileSafety.ApplyAsync(NewFile(), new Uri("https://example.org/a"), null, log.Add, TestContext.Current.CancellationToken, markWriter: (_, _) => false);

        flagged.Should().BeFalse("a safety mark the volume cannot hold never fails the download");
        log.Should().Equal("Mark-of-the-Web not supported on this volume");
    }

    [Fact]
    public async Task An_ftp_transfer_is_marked_with_zone_3_and_no_host_url_and_is_scanned_before_the_rename()
    {
        var data = GeneratedContent_Bytes(300_000);
        var scanner = new Scanner(ScanResult.Clean);
        var log = new ConcurrentQueue<string>();
        var options = new HttpTransferOptions { Scanner = scanner, Log = log.Enqueue };
        options = options with { SchedulerOptions = options.SchedulerOptions with { MaxConnections = 1, InitialConnections = 1, MinLeaseBytes = data.Length, MaxLeaseBytes = data.Length } };

        var outcome = await new HttpTransferRunner().SegmentedAsync(
            new MemoryTransport(data), new Uri("ftp://user:secret@files.example.org/pub/a.bin"), null, _folder, "a.bin", data.Length,
            null, null, null, options, TimeProvider.System, TestContext.Current.CancellationToken, rangesSupported: false);

        outcome.Should().BeOfType<TransferCompleted>();
        scanner.Calls.Should().Be(1);
        log.Should().Contain("Scan: clean");
        var mark = await File.ReadAllTextAsync(Path.Combine(_folder, "a.bin") + ":Zone.Identifier", TestContext.Current.CancellationToken);
        mark.Should().Contain("ZoneId=3").And.NotContain("HostUrl").And.NotContain("secret");
    }

    [Fact]
    public async Task A_flagged_ftp_transfer_fails_and_nothing_is_renamed()
    {
        var data = GeneratedContent_Bytes(200_000);
        var options = new HttpTransferOptions { Scanner = new Scanner(ScanResult.Detected) };
        options = options with { SchedulerOptions = options.SchedulerOptions with { MaxConnections = 1, InitialConnections = 1, MinLeaseBytes = data.Length, MaxLeaseBytes = data.Length } };

        var outcome = await new HttpTransferRunner().SegmentedAsync(
            new MemoryTransport(data), new Uri("ftp://files.example.org/pub/b.bin"), null, _folder, "b.bin", data.Length,
            null, null, null, options, TimeProvider.System, TestContext.Current.CancellationToken, rangesSupported: false);

        outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.FlaggedByAntivirus);
        File.Exists(Path.Combine(_folder, "b.bin")).Should().BeFalse();
    }

    private static byte[] GeneratedContent_Bytes(int size)
    {
        var bytes = new byte[size];
        new Random(8).NextBytes(bytes);
        return bytes;
    }
}
