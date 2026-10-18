namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-004")]
public sealed class WorkerWakeSignalTests
{
    [Fact]
    public async Task Wakes_that_nobody_waited_for_never_pile_up_beyond_one_per_worker()
    {
        const int workers = 8;
        using var signal = new WorkerWakeSignal(workers);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 1_000; i++)
        {
            signal.Wake(workers);
            signal.Wake();
        }

        for (var i = 0; i < workers; i++)
        {
            await signal.WaitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        }

        var extra = signal.WaitAsync(ct);
        await Task.Delay(100, ct);
        extra.IsCompleted.Should().BeFalse("only one stored wake per worker may remain, or idle workers spin through a backlog");
    }

    [Fact]
    public async Task A_wake_releases_a_waiting_worker()
    {
        using var signal = new WorkerWakeSignal(4);
        var ct = TestContext.Current.CancellationToken;
        var waiting = signal.WaitAsync(ct);
        waiting.IsCompleted.Should().BeFalse();

        signal.Wake();

        await waiting.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
