using System.Diagnostics;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

/// <summary>
/// Caps shared by several jobs and caps that change while a download runs (KASI-BWL-001, KASI-BWL-002, TC-ENG-024), measured
/// on the limiter's own byte counters over steady windows after a settling period. Own class so these long runs overlap the
/// others instead of lengthening <see cref="HttpTransferBandwidthTests"/>.
/// </summary>
[Collection(BandwidthRateGroup.Name)]
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-BWL-001")]
[Trait("Requirement", "KASI-BWL-002")]
[Trait("TC", "TC-ENG-024")]
public sealed class HttpTransferBandwidthSharingTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-bandwidth-sharing-" + Guid.NewGuid().ToString("N"));
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
    public async Task A_rate_change_while_downloading_moves_the_download_to_the_new_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        var limiter = new BandwidthLimiter(200_000);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var download = StartDownload("rate-change", 6_000_000, limiter, job: null, stop.Token);
        await WaitForBytesAsync(() => limiter.ConsumedBytes, ct);

        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var slow = await BandwidthMeasure.MeasureAsync(() => limiter.ConsumedBytes, TimeSpan.FromSeconds(4), ct);
        limiter.SetGlobalRate(500_000);
        await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
        var fast = await BandwidthMeasure.MeasureAsync(() => limiter.ConsumedBytes, TimeSpan.FromSeconds(4), ct);

        BandwidthMeasure.AssertAtCap(slow, 200_000, "before the change");
        BandwidthMeasure.AssertAtCap(fast, 500_000, "after the change");
        fast.Rate.Should().BeGreaterThan(slow.Rate * 1.5, "the new cap, 2.5 times the old one, must take effect");
        await stop.CancelAsync();
        (await download).Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
    }

    [Fact]
    public async Task Two_jobs_share_the_global_cap_and_the_capped_one_keeps_its_own()
    {
        var ct = TestContext.Current.CancellationToken;
        var limiter = new BandwidthLimiter(500_000);
        var cappedJob = limiter.CreateJobLimit(200_000);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var capped = StartDownload("shared-capped", 6_000_000, limiter, cappedJob, stop.Token);
        var free = StartDownload("shared-free", 6_000_000, limiter, job: null, stop.Token);
        await WaitForBytesAsync(() => cappedJob.ConsumedBytes, ct);

        var start = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var cappedWindow = BandwidthMeasure.MeasureAsync(() => cappedJob.ConsumedBytes, TimeSpan.FromSeconds(8), ct);
        var total = await BandwidthMeasure.MeasureAsync(() => limiter.ConsumedBytes, TimeSpan.FromSeconds(8), ct);
        var cappedMeasured = await cappedWindow;

        BandwidthMeasure.AssertAtCap(total, 500_000);
        // The global bucket is first come, first served (ADR 0010), so the capped job's share of it is not guaranteed: it never
        // exceeds its own cap (plus one bucket), and it keeps a real part of its share while the other job takes the rest.
        cappedMeasured.Rate.Should().BeLessThanOrEqualTo(200_000 + ((50_000d + BandwidthLimiter.MaximumReadBytes) / cappedMeasured.Seconds), "the job cap holds");
        cappedMeasured.Rate.Should().BeGreaterThan(60_000, "the capped job is not starved by the free job");
        await stop.CancelAsync();
        (await capped).Should().BeOfType<TransferFailed>();
        (await free).Should().BeOfType<TransferFailed>();
        Stopwatch.GetElapsedTime(start).Should().BeLessThan(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task A_capped_download_reads_every_byte_once_because_it_has_no_endgame_duplicate_tail()
    {
        var ct = TestContext.Current.CancellationToken;
        const int size = 6_000_000;
        var limiter = new BandwidthLimiter(3_000_000);

        var outcome = await StartDownload("no-endgame", size, limiter, job: null, ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(completed.FinalPath, ct)))
            .Should().Be(GeneratedContent.Sha256Hex("no-endgame", size));
        limiter.ConsumedBytes.Should().Be(size, "a duplicated tail would read some bytes twice and spend capped bandwidth on them");
    }

    private Task<TransferOutcome> StartDownload(string seed, int size, BandwidthLimiter limiter, BandwidthLimiter.JobBandwidthLimit? job, CancellationToken cancellationToken) =>
        new HttpTransferRunner().DownloadAsync(
            new Uri(_host.BaseAddress, $"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions
            {
                BandwidthLimiter = limiter,
                JobBandwidthLimit = job,
                OverrideFileName = seed + ".bin",
                SchedulerOptions = new SegmentSchedulerOptions { MaxConnections = 4, InitialConnections = 4 },
            },
            cancellationToken);

    private static async Task WaitForBytesAsync(Func<long> consumed, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp();
        while (consumed() == 0)
        {
            Stopwatch.GetElapsedTime(deadline).Should().BeLessThan(TimeSpan.FromSeconds(30), "the download must start");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }
}
