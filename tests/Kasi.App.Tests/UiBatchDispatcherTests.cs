using Kasi.App.Threading;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-NFR-006")]
public class UiBatchDispatcherTests
{
    private static readonly TimeSpan _period = TimeSpan.FromMilliseconds(250);

    private sealed record Progress(int Job, long Bytes);

    private sealed class Applied
    {
        private readonly List<IReadOnlyCollection<Progress>> _batches = [];
        private readonly System.Threading.Channels.Channel<bool> _signal = System.Threading.Channels.Channel.CreateUnbounded<bool>();

        public IReadOnlyList<IReadOnlyCollection<Progress>> Batches
        {
            get
            {
                lock (_batches)
                {
                    return [.. _batches];
                }
            }
        }

        public void Apply(IReadOnlyCollection<Progress> batch)
        {
            lock (_batches)
            {
                _batches.Add([.. batch]);
            }

            _signal.Writer.TryWrite(true);
        }

        public Task<bool> WaitForBatchAsync(CancellationToken ct) => _signal.Reader.ReadAsync(ct).AsTask();
    }

    [Fact]
    public async Task Ten_thousand_updates_for_fifty_jobs_between_two_ticks_are_applied_once_as_fifty_items_with_the_latest_values()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var applied = new Applied();
        await using var dispatcher = new UiBatchDispatcher<int, Progress>(applied.Apply, _period, clock);
        dispatcher.Start();

        for (var i = 0; i < 10_000; i++)
        {
            dispatcher.Post(i % 50, new Progress(i % 50, i));
        }

        clock.Advance(_period);
        await applied.WaitForBatchAsync(ct);

        var batch = applied.Batches.Should().ContainSingle().Which;
        batch.Should().HaveCount(50);
        batch.Select(p => p.Job).Should().BeEquivalentTo(Enumerable.Range(0, 50));
        batch.Should().OnlyContain(p => p.Bytes >= 9_950, "the latest update of each job wins");
    }

    [Fact]
    public async Task Nothing_is_applied_without_a_tick_and_an_empty_tick_applies_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var applied = new Applied();
        await using var dispatcher = new UiBatchDispatcher<int, Progress>(applied.Apply, _period, clock);
        dispatcher.Start();

        dispatcher.Post(1, new Progress(1, 1));
        clock.Advance(_period - TimeSpan.FromMilliseconds(1)); // the fake clock has not reached the first tick
        await Task.Delay(50, ct); // an absence check: nothing can arrive, however long it waits
        applied.Batches.Should().BeEmpty();

        clock.Advance(TimeSpan.FromMilliseconds(1));
        await applied.WaitForBatchAsync(ct);
        clock.Advance(_period * 3); // ticks with nothing posted
        await Task.Delay(50, ct);

        applied.Batches.Should().ContainSingle().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task Updates_after_a_batch_go_into_the_next_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var applied = new Applied();
        await using var dispatcher = new UiBatchDispatcher<int, Progress>(applied.Apply, _period, clock);
        dispatcher.Start();

        dispatcher.Post(1, new Progress(1, 10));
        clock.Advance(_period);
        await applied.WaitForBatchAsync(ct);
        dispatcher.Post(1, new Progress(1, 20));
        clock.Advance(_period);
        await applied.WaitForBatchAsync(ct);

        applied.Batches.Select(b => b.Single().Bytes).Should().Equal(10, 20);
    }

    [Fact]
    public async Task After_disposal_nothing_is_applied()
    {
        var clock = new FakeTimeProvider();
        var applied = new Applied();
        var dispatcher = new UiBatchDispatcher<int, Progress>(applied.Apply, _period, clock);
        dispatcher.Start();
        dispatcher.Post(1, new Progress(1, 1));

        await dispatcher.DisposeAsync();
        clock.Advance(_period * 4);

        applied.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_apply_is_logged_by_type_only_and_the_loop_carries_on()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        var logger = new CapturingLogger();
        var applied = new Applied();
        var calls = 0;
        await using var dispatcher = new UiBatchDispatcher<int, Progress>(
            batch =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    throw new InvalidOperationException(@"secret C:\Users\someone\file.zip");
                }

                applied.Apply(batch);
            },
            _period,
            clock,
            logger);
        dispatcher.Start();

        dispatcher.Post(1, new Progress(1, 1));
        clock.Advance(_period);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1 && logger.Lines.Count == 1, ct);
        dispatcher.Post(2, new Progress(2, 2));
        clock.Advance(_period);
        await applied.WaitForBatchAsync(ct);

        applied.Batches.Should().ContainSingle().Which.Should().ContainSingle().Which.Job.Should().Be(2);
        logger.Lines.Should().ContainSingle().Which.Should().Contain("InvalidOperationException").And.NotContain("secret").And.NotContain("Users");
    }

    // the loop runs on another thread; wait for its effect (the test result does not depend on how long that takes)
    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(10, ct);
        }
    }
}
