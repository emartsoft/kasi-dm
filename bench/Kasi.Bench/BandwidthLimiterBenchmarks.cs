using BenchmarkDotNet.Attributes;
using Kasi.Engine;

namespace Kasi.Bench;

/// <summary>
/// Measures the completed-ValueTask path used before every 16 KiB network read (KASI-BWL-001, KASI-BWL-002).
/// The very high caps keep the bucket full so this measures bookkeeping rather than an intentional timer wait.
/// Run with <c>dotnet run -c Release --project bench/Kasi.Bench -- --filter *BandwidthLimiter*</c>.
/// </summary>
[MemoryDiagnoser]
public class BandwidthLimiterBenchmarks
{
    private readonly BandwidthLimiter _unlimited = new();
    private readonly BandwidthLimiter _limiter = new(long.MaxValue);
    private readonly BandwidthLimiter.JobBandwidthLimit _job;

    public BandwidthLimiterBenchmarks()
    {
        _job = _limiter.CreateJobLimit(long.MaxValue);
    }

    [Benchmark(Baseline = true)]
    public ValueTask<int> UnlimitedBucketAsync() => _unlimited.AcquireGlobalAsync(BandwidthLimiter.MaximumReadBytes);

    [Benchmark]
    public ValueTask<int> GlobalBucketAsync() => _limiter.AcquireGlobalAsync(BandwidthLimiter.MaximumReadBytes);

    [Benchmark]
    public ValueTask<int> GlobalAndJobBucketsAsync() => _limiter.AcquireAsync(BandwidthLimiter.MaximumReadBytes, _job);
}
