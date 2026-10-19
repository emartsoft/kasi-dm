using System.Security.Cryptography;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

[Collection(BandwidthRateGroup.Name)]
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-BWL-001")]
[Trait("Requirement", "KASI-BWL-002")]
[Trait("TC", "TC-ENG-024")]
public sealed class HttpTransferBandwidthTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-bandwidth-" + Guid.NewGuid().ToString("N"));
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
    public async Task Segmented_HTTP_download_obeys_500KB_per_second_global_cap_over_ten_seconds()
    {
        // The file outlasts the window by a wide margin, so a late timer on a busy runner cannot end the download inside it.
        const int size = 12_000_000;
        const string seed = "bandwidth-global";
        var time = TimeProvider.System;
        var limiter = new BandwidthLimiter(500_000);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var download = new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions
            {
                BandwidthLimiter = limiter,
                SchedulerOptions = new SegmentSchedulerOptions { InitialConnections = 1 },
            },
            stop.Token);

        while (limiter.ConsumedBytes == 0 && !download.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        limiter.ConsumedBytes.Should().BeGreaterThan(0);
        var before = limiter.ConsumedBytes;
        var start = time.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var tenSecondRate = (limiter.ConsumedBytes - before) / time.GetElapsedTime(start).TotalSeconds;
        await stop.CancelAsync();
        (await download).Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
        tenSecondRate.Should().BeInRange(475_000, 525_000);
    }

    [Fact]
    public async Task A_200KB_per_second_job_cap_does_not_limit_another_job()
    {
        const int cappedSize = 6_000_000;
        const int freeSize = 5_000_000;
        const string cappedSeed = "bandwidth-capped-job";
        const string freeSeed = "bandwidth-free-job";
        var time = TimeProvider.System;
        var limiter = new BandwidthLimiter();
        var job = limiter.CreateJobLimit(200_000);
        var start = time.GetTimestamp();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cappedTask = new HttpTransferRunner().DownloadAsync(
            Url($"/norange/{cappedSeed}/{cappedSize}"),
            _folder,
            new HttpTransferOptions { BandwidthLimiter = limiter, JobBandwidthLimit = job },
            stop.Token);
        var measuredFreeTask = DownloadFreeAsync();

        while (job.ConsumedBytes == 0 && !cappedTask.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        job.ConsumedBytes.Should().BeGreaterThan(0);
        var before = job.ConsumedBytes;
        var windowStart = time.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var windowSeconds = time.GetElapsedTime(windowStart).TotalSeconds;
        var tenSecondRate = (job.ConsumedBytes - before) / windowSeconds;
        await stop.CancelAsync();

        var (freeOutcome, freeSeconds) = await measuredFreeTask;
        var free = freeOutcome.Should().BeOfType<TransferCompleted>().Subject;
        (await cappedTask).Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.Cancelled);

        tenSecondRate.Should().BeInRange(190_000, 210_000);
        freeSeconds.Should().BeLessThan(windowSeconds / 2, "the capped job must not slow the free one");
        await AssertHashAsync(free.FinalPath, freeSeed, freeSize);

        async Task<(TransferOutcome Outcome, double Seconds)> DownloadFreeAsync()
        {
            var outcome = await new HttpTransferRunner().DownloadAsync(
                Url($"/files/{freeSeed}/{freeSize}"),
                _folder,
                new HttpTransferOptions { BandwidthLimiter = limiter },
                TestContext.Current.CancellationToken);
            return (outcome, time.GetElapsedTime(start).TotalSeconds);
        }
    }

    [Fact]
    public async Task Waiting_for_bandwidth_does_not_trigger_the_network_stall_timer()
    {
        const string seed = "bandwidth-slow";
        const int size = 3;
        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/norange/{seed}/{size}"),
            _folder,
            new HttpTransferOptions
            {
                BandwidthLimiter = new BandwidthLimiter(1),
                RetryPolicy = new TransferRetryPolicy { ReadStallTimeout = TimeSpan.FromMilliseconds(500) },
            },
            TestContext.Current.CancellationToken);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        await AssertHashAsync(completed.FinalPath, seed, size);
    }

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private static async Task AssertHashAsync(string path, string seed, int size)
    {
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }
}
