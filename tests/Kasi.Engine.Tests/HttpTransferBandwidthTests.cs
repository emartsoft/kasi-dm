using System.Security.Cryptography;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

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
        const int size = 5_300_000;
        const string seed = "bandwidth-global";
        var time = TimeProvider.System;
        var limiter = new BandwidthLimiter(500_000);
        var download = new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions
            {
                BandwidthLimiter = limiter,
                SchedulerOptions = new SegmentSchedulerOptions { InitialConnections = 1 },
            },
            TestContext.Current.CancellationToken);

        while (limiter.ConsumedBytes == 0 && !download.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        limiter.ConsumedBytes.Should().BeGreaterThan(0);
        var start = time.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var tenSecondRate = limiter.ConsumedBytes / time.GetElapsedTime(start).TotalSeconds;
        tenSecondRate.Should().BeInRange(475_000, 525_000);
        var completed = (await download).Should().BeOfType<TransferCompleted>().Subject;
        await AssertHashAsync(completed.FinalPath, seed, size);
    }

    [Fact]
    public async Task A_200KB_per_second_job_cap_does_not_limit_another_job()
    {
        const int cappedSize = 2_200_000;
        const int freeSize = 5_000_000;
        const string cappedSeed = "bandwidth-capped-job";
        const string freeSeed = "bandwidth-free-job";
        var time = TimeProvider.System;
        var limiter = new BandwidthLimiter();
        var job = limiter.CreateJobLimit(200_000);
        var start = time.GetTimestamp();
        var cappedTask = new HttpTransferRunner().DownloadAsync(
            Url($"/norange/{cappedSeed}/{cappedSize}"),
            _folder,
            new HttpTransferOptions { BandwidthLimiter = limiter, JobBandwidthLimit = job },
            TestContext.Current.CancellationToken);
        var measuredFreeTask = DownloadFreeAsync();

        while (job.ConsumedBytes == 0 && !cappedTask.IsCompleted)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        job.ConsumedBytes.Should().BeGreaterThan(0);
        var windowStart = time.GetTimestamp();
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var tenSecondRate = job.ConsumedBytes / time.GetElapsedTime(windowStart).TotalSeconds;
        tenSecondRate.Should().BeInRange(190_000, 210_000);

        var (freeOutcome, freeSeconds) = await measuredFreeTask;
        var free = freeOutcome.Should().BeOfType<TransferCompleted>().Subject;
        var capped = (await cappedTask).Should().BeOfType<TransferCompleted>().Subject;
        var cappedSeconds = time.GetElapsedTime(start).TotalSeconds;

        freeSeconds.Should().BeLessThan(cappedSeconds / 2);
        await AssertHashAsync(capped.FinalPath, cappedSeed, cappedSize);
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
