using System.Collections.Concurrent;
using System.Text;
using Kasi.Core;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

/// <summary>11e (docs/plans/m2-11.md): the optional scan of a finished download. KASI-CAT-004, TC-CAT-004.</summary>
[Trait("Requirement", "KASI-CAT-004")]
[Trait("TC", "TC-CAT-004")]
public sealed class FileScannerTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    private sealed class FakeScanner(bool enabled, Func<string, ScanResult> answer) : IFileScanner
    {
        public bool IsEnabled { get; } = enabled;

        public List<string> Asked { get; } = [];

        public Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken)
        {
            Asked.Add(Path.GetFileName(path));
            return Task.FromResult(answer(path));
        }
    }

    private sealed class ThrowingScanner : IFileScanner
    {
        public bool IsEnabled => true;

        public Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("provider error");
    }

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-scan-" + Guid.NewGuid().ToString("N"));
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

    private async Task<(TransferOutcome Outcome, List<string> Log)> DownloadAsync(IFileScanner? scanner, string seed = "scan", int size = 1_048_576)
    {
        var log = new ConcurrentQueue<string>();
        var outcome = await new HttpTransferRunner().DownloadAsync(
            new Uri(_host.BaseAddress, $"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions { Scanner = scanner, Log = log.Enqueue },
            TestContext.Current.CancellationToken);
        return (outcome, [.. log]);
    }

    [Fact]
    public async Task With_no_scanner_or_a_disabled_one_nothing_is_asked_and_nothing_is_logged()
    {
        var disabled = new FakeScanner(enabled: false, _ => ScanResult.Detected);

        var (outcome, log) = await DownloadAsync(disabled);

        outcome.Should().BeOfType<TransferCompleted>();
        disabled.Asked.Should().BeEmpty("off is the default and it costs nothing");
        log.Should().NotContain(l => l.StartsWith("Scan:", StringComparison.Ordinal));
        var (none, _) = await DownloadAsync(null, "scan-none", 1_048_577);
        none.Should().BeOfType<TransferCompleted>();
    }

    [Fact]
    public async Task A_clean_file_completes_and_the_log_says_so()
    {
        var scanner = new FakeScanner(true, _ => ScanResult.Clean);

        var (outcome, log) = await DownloadAsync(scanner);

        outcome.Should().BeOfType<TransferCompleted>();
        scanner.Asked.Should().ContainSingle().Which.Should().EndWith(".kasipart", "the file is scanned before it gets its final name");
        log.Should().Contain("Scan: clean");
        Directory.GetFiles(_folder).Should().ContainSingle(f => !f.EndsWith(".kasipart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_detection_fails_the_job_with_a_fixed_message_and_leaves_the_file_alone()
    {
        var scanner = new FakeScanner(true, _ => ScanResult.Detected);

        var (outcome, log) = await DownloadAsync(scanner);

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.FlaggedByAntivirus);
        failed.Detail.Should().Be("Flagged by antivirus. Kasi leaves the file where the antivirus put it.");
        log.Should().Contain("Scan: flagged by antivirus");
        Directory.GetFiles(_folder).Should().ContainSingle(f => f.EndsWith(".kasipart", StringComparison.Ordinal), "nothing was renamed and nothing was deleted");
        log.Should().OnlyContain(l => !l.Contains(_folder, StringComparison.OrdinalIgnoreCase) && !l.Contains("127.0.0.1", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("file too large to scan here")]
    [InlineData("AMSI is not available on this system")]
    public async Task A_scan_that_cannot_run_never_fails_the_download(string note)
    {
        var scanner = new FakeScanner(true, _ => ScanResult.NotScanned(note));

        var (outcome, log) = await DownloadAsync(scanner);

        outcome.Should().BeOfType<TransferCompleted>();
        log.Should().Contain($"Scan: not scanned ({note})");
    }

    [Fact]
    public async Task A_scanner_that_throws_is_the_same_as_not_scanned()
    {
        var (outcome, log) = await DownloadAsync(new ThrowingScanner());

        outcome.Should().BeOfType<TransferCompleted>();
        log.Should().Contain("Scan: not scanned (the scan failed)");
    }

    // AMSI itself. The EICAR test string is assembled here at run time and is never saved as a file, so no antivirus quarantines the checkout.
    [Fact]
    public void The_real_AMSI_wrapper_reports_the_EICAR_string_as_detected()
    {
        var eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$" + "EICAR-STANDARD-ANTIVIRUS-TEST-FILE!" + "$H+H*");

        var result = AmsiScanner.ScanBuffer(eicar, "eicar.com");

        Assert.SkipWhen(result.Verdict == ScanVerdict.NotScanned, "no antimalware provider answered AMSI here: " + result.Note);
        result.Verdict.Should().Be(ScanVerdict.Detected);
        AmsiScanner.ScanBuffer("an ordinary sentence"u8, "text.txt").Verdict.Should().Be(ScanVerdict.Clean);
    }

    [Fact]
    public async Task The_real_AMSI_scan_reads_a_file_longer_than_one_window_and_leaves_an_empty_one_alone()
    {
        var path = Path.Combine(_folder, "big.bin");
        await File.WriteAllBytesAsync(path, GeneratedContent.Bytes("amsi-big", AmsiScanner.WindowBytes + 100_000), TestContext.Current.CancellationToken);

        var result = await new AmsiScanner().ScanAsync(path, TestContext.Current.CancellationToken);

        Assert.SkipWhen(result.Verdict == ScanVerdict.NotScanned, "no antimalware provider answered AMSI here: " + result.Note);
        result.Verdict.Should().Be(ScanVerdict.Clean);
        var empty = Path.Combine(_folder, "empty.bin");
        await File.WriteAllBytesAsync(empty, [], TestContext.Current.CancellationToken);
        (await new AmsiScanner().ScanAsync(empty, TestContext.Current.CancellationToken)).Verdict.Should().Be(ScanVerdict.NotScanned);
    }
}
