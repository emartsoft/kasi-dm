using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-BWL-001")]
[Trait("Requirement", "KASI-BWL-002")]
[Trait("TC", "TC-ENG-024")]
public sealed class BandwidthLimiterTests
{
    [Fact]
    public async Task A_read_is_at_most_sixteen_kib_and_the_initial_burst_is_a_quarter_second()
    {
        var limiter = new BandwidthLimiter(500_000, new FakeTimeProvider());
        (await limiter.AcquireGlobalAsync(1_000_000, TestContext.Current.CancellationToken)).Should().Be(16_384);
        for (var read = 1; read < 7; read++)
        {
            (await limiter.AcquireGlobalAsync(16_384, TestContext.Current.CancellationToken)).Should().Be(16_384);
        }

        using var cancellation = new CancellationTokenSource();
        var beyondBurst = limiter.AcquireGlobalAsync(16_384, cancellation.Token);
        beyondBurst.IsCompleted.Should().BeFalse("seven reads use 114,688 bytes of the 125,000-byte burst");
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await beyondBurst);
    }

    [Fact]
    public async Task Global_and_job_caps_hold_within_five_percent_over_ten_seconds()
    {
        var time = new FakeTimeProvider();
        var limiter = new BandwidthLimiter(500_000, time);
        var cappedJob = limiter.CreateJobLimit(200_000);
        long cappedBytes = 0;
        long freeBytes = 0;

        for (var tick = 0; tick < 100; tick++)
        {
            cappedBytes += await DrainAvailableAsync(limiter, cappedJob);
            freeBytes += await DrainAvailableAsync(limiter, null);
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        var total = cappedBytes + freeBytes;
        total.Should().BeInRange(4_750_000, 5_250_000);
        cappedBytes.Should().BeInRange(1_900_000, 2_100_000);
        freeBytes.Should().BeGreaterThan(cappedBytes, "the uncapped job can use the global budget left by the capped job");
    }

    [Fact]
    public async Task Waiting_on_a_job_cap_does_not_reserve_global_bytes()
    {
        var time = new FakeTimeProvider();
        var limiter = new BandwidthLimiter(500_000, time);
        var job = limiter.CreateJobLimit(65_536);
        (await limiter.AcquireAsync(16_384, job, TestContext.Current.CancellationToken)).Should().Be(16_384);

        using var cancellation = new CancellationTokenSource();
        var waiting = limiter.AcquireAsync(16_384, job, cancellation.Token);
        waiting.IsCompleted.Should().BeFalse();
        (await limiter.AcquireGlobalAsync(16_384, TestContext.Current.CancellationToken)).Should().Be(16_384);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waiting);
    }

    [Fact]
    public async Task Unused_bytes_are_returned_to_both_buckets()
    {
        var limiter = new BandwidthLimiter(65_536, new FakeTimeProvider());
        var job = limiter.CreateJobLimit(65_536);
        (await limiter.AcquireAsync(16_384, job, TestContext.Current.CancellationToken)).Should().Be(16_384);
        limiter.ReturnUnused(8_192, job);
        (await limiter.AcquireAsync(8_192, job, TestContext.Current.CancellationToken)).Should().Be(8_192);
    }

    [Fact]
    public async Task Rate_changes_wake_waiters_and_a_small_quarter_second_bucket_still_makes_progress()
    {
        var time = new FakeTimeProvider();
        var limiter = new BandwidthLimiter(1, time);
        (await limiter.AcquireGlobalAsync(16_384, TestContext.Current.CancellationToken)).Should().Be(1);
        var waiting = limiter.AcquireGlobalAsync(16_384, TestContext.Current.CancellationToken);
        waiting.IsCompleted.Should().BeFalse();

        limiter.SetGlobalRate(null);
        (await waiting.AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).Should().Be(16_384);
        limiter.GlobalBytesPerSecond.Should().BeNull();

        var job = limiter.CreateJobLimit(1);
        (await limiter.AcquireAsync(16_384, job, TestContext.Current.CancellationToken)).Should().Be(1);
        var jobWaiting = limiter.AcquireAsync(16_384, job, TestContext.Current.CancellationToken);
        jobWaiting.IsCompleted.Should().BeFalse();
        job.SetRate(null);
        (await jobWaiting.AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).Should().Be(16_384);
    }

    [Fact]
    public void Job_budget_must_belong_to_the_configured_limiter()
    {
        var otherJob = new BandwidthLimiter().CreateJobLimit(200_000);
        var options = new HttpTransferOptions
        {
            BandwidthLimiter = new BandwidthLimiter(),
            JobBandwidthLimit = otherJob,
        };

        var action = options.Validate;
        action.Should().Throw<ArgumentException>();
    }

    private static async Task<long> DrainAvailableAsync(BandwidthLimiter limiter, BandwidthLimiter.JobBandwidthLimit? job)
    {
        long total = 0;
        using var cancellation = new CancellationTokenSource();
        while (true)
        {
            var permit = limiter.AcquireAsync(BandwidthLimiter.MaximumReadBytes, job, cancellation.Token);
            if (!permit.IsCompletedSuccessfully)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await permit);
                return total;
            }

            total += await permit;
        }
    }
}
