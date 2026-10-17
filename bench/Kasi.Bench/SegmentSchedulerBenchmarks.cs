using BenchmarkDotNet.Attributes;
using Kasi.Engine;

namespace Kasi.Bench;

/// <summary>
/// Micro-benchmarks of the scheduler calls that sit on the transfer path (TRD 6.2; M1.5). <see cref="SegmentScheduler.AdvanceLease"/>
/// runs once per received chunk and must not allocate; <see cref="SegmentScheduler.TryAcquireNextLease"/> runs once per lease.
/// Run with <c>dotnet run -c Release --project bench/Kasi.Bench -- --filter *SegmentScheduler*</c>.
/// </summary>
[MemoryDiagnoser]
public class SegmentSchedulerBenchmarks
{
    private const int ChunkBytes = 16 * 1024;
    private const long FileBytes = 64L * 1024 * 1024 * 1024;

    private SegmentScheduler _scheduler = null!;
    private long _leaseId;
    private long _remainingInLease;

    [GlobalSetup(Targets = [nameof(AdvanceLease)])]
    public void SetUpAdvance()
    {
        _scheduler = new SegmentScheduler(FileBytes);
        StartLease();
    }

    /// <summary>One chunk accepted into a lease; a new lease is taken when it is used up, which the mean amortises over the chunks of a lease.</summary>
    [Benchmark]
    public LeaseAdvanceResult AdvanceLease()
    {
        if (_remainingInLease < ChunkBytes)
        {
            _scheduler.CompleteLease(_leaseId);
            StartLease();
        }

        _remainingInLease -= ChunkBytes;
        return _scheduler.AdvanceLease(_leaseId, ChunkBytes);
    }

    /// <summary>
    /// Worker 0 takes, advances and completes a lease, and worker 1 then takes a lease by splitting the large segment. Both workers
    /// are allowed from the start (<c>InitialConnections = 2</c>); with the default of one, worker 1 would be refused and no split would happen.
    /// </summary>
    [Benchmark]
    public bool AcquireAdvanceComplete()
    {
        var scheduler = new SegmentScheduler(FileBytes, options: new SegmentSchedulerOptions { InitialConnections = 2 });
        var lease = scheduler.TryAcquireNextLease(0)!.Value;
        scheduler.AdvanceLease(lease.LeaseId, (int)lease.Length);
        scheduler.CompleteLease(lease.LeaseId);
        var split = scheduler.TryAcquireNextLease(1);
        return split is not null && scheduler.StructureVersion == 1;
    }

    private void StartLease()
    {
        // the benchmark moves far more data than the file holds, so start over when the file is done
        var lease = _scheduler.TryAcquireNextLease(0) ?? (_scheduler = new SegmentScheduler(FileBytes)).TryAcquireNextLease(0)!.Value;
        _leaseId = lease.LeaseId;
        _remainingInLease = lease.Length;
    }
}
