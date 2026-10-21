using System.Diagnostics;
using System.Globalization;

namespace Kasi.Bench.Macro;

/// <summary>What a sampled window looked like.</summary>
public sealed record ResourceReading(double PeakWorkingSetMegabytes, double CpuPercentOfOneCore, TimeSpan Duration);

/// <summary>
/// Samples this process's working set and processor time every 500 ms. These are engine-only early signals for KASI-NFR-004 and
/// KASI-NFR-005: the requirements are about the whole app, and the real gate is M2 and later. The server runs in another
/// process, so it is not in these numbers. <see cref="Environment.WorkingSet"/> includes shared pages, so it is an upper bound for the
/// private working set the requirement names.
/// </summary>
public sealed class ResourceSampler : IAsyncDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _cpuAtStart;
    private readonly Task _loop;
    private long _peak;

    public ResourceSampler(TimeSpan? interval = null)
    {
        _cpuAtStart = Cpu();
        _peak = WorkingSet();
        var period = interval ?? TimeSpan.FromMilliseconds(500);
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(period);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _peak, Math.Max(Interlocked.Read(ref _peak), WorkingSet()));
                }
            }
            catch (OperationCanceledException)
            {
                // stopped
            }
        });
    }

    public async Task<ResourceReading> StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // the loop task is started by this class's constructor
        await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        var elapsed = _clock.Elapsed;
        var cpu = Cpu() - _cpuAtStart;
        Interlocked.Exchange(ref _peak, Math.Max(Interlocked.Read(ref _peak), WorkingSet()));
        return new ResourceReading(Interlocked.Read(ref _peak) / (1024d * 1024d), CpuPercent(cpu, elapsed), elapsed);
    }

    /// <summary>Processor time over wall-clock time, as a percentage of one core.</summary>
    public static double CpuPercent(TimeSpan cpu, TimeSpan elapsed) => elapsed <= TimeSpan.Zero ? 0 : cpu.TotalSeconds / elapsed.TotalSeconds * 100;

    public static string Describe(ResourceReading reading) =>
        string.Create(CultureInfo.InvariantCulture, $"peak working set {reading.PeakWorkingSetMegabytes:0} MB, CPU {reading.CpuPercentOfOneCore:0.0}% of one core over {reading.Duration.TotalSeconds:0} s");

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
#pragma warning disable VSTHRD003 // the loop task is started by this class's constructor
        await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        _stop.Dispose();
        _process.Dispose();
    }

    // Environment.WorkingSet and TotalProcessorTime read the process through its handle. Process.Refresh and WorkingSet64
    // enumerate every process on the machine, which cost about a third of a core when sampled twice a second.
    private static long WorkingSet() => Environment.WorkingSet;

    private TimeSpan Cpu() => _process.TotalProcessorTime;
}
