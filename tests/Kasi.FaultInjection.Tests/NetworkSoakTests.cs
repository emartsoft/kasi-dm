using Kasi.FaultInjection;

namespace Kasi.FaultInjection.Tests;

public sealed class NetworkSoakTests
{
    [Fact]
    public void The_schedule_depends_on_the_seed_only_and_spreads_the_jobs_over_the_window()
    {
        var window = TimeSpan.FromHours(2);

        var first = NetworkSoakSuite.Plan(7, 50, window);
        var again = NetworkSoakSuite.Plan(7, 50, window);
        var other = NetworkSoakSuite.Plan(8, 50, window);

        first.Should().Equal(again);
        first.Should().NotEqual(other);
        first.Should().HaveCount(50);
        first.Select(p => p.StartAfter).Should().BeInAscendingOrder().And.OnlyContain(t => t >= TimeSpan.Zero && t < window);
        first.Should().OnlyContain(p => p.LatencyMilliseconds >= 100 && p.LatencyMilliseconds <= 500);
    }

    [Fact]
    public async Task Without_a_daemon_the_soak_says_so_instead_of_passing()
    {
        var saved = Environment.GetEnvironmentVariable("KASI_TOXIPROXY");
        Environment.SetEnvironmentVariable("KASI_TOXIPROXY", null);
        try
        {
            var report = await FaultInjectionRunner.RunToReportAsync(
                new FaultInjectionOptions { Scenario = "network-soak", Seed = 1, Iterations = 1 },
                TestContext.Current.CancellationToken);

            report.Skipped.Should().BeTrue();
            report.Passed.Should().BeFalse();
            report.SkipReason.Should().Contain("Toxiproxy");
        }
        finally
        {
            Environment.SetEnvironmentVariable("KASI_TOXIPROXY", saved);
        }
    }

    [Trait("Category", "Integration")]
    [Trait("TC", "TC-RES-004")]
    [Trait("Requirement", "KASI-RES-004")]
    [Fact]
    public async Task A_short_soak_through_a_real_toxiproxy_finishes_every_job()
    {
        var endpoint = Environment.GetEnvironmentVariable("KASI_TOXIPROXY");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(endpoint), "set KASI_TOXIPROXY=host:port to run this against a Toxiproxy daemon");
        var options = new FaultInjectionOptions
        {
            Scenario = "network-soak",
            Seed = 31,
            Iterations = 3,
            DurationSeconds = 20,
            Size = 16L * 1024 * 1024,
            Rate = 4L * 1024 * 1024,
            Toxiproxy = endpoint,
        };

        var report = await FaultInjectionRunner.RunToReportAsync(options, TestContext.Current.CancellationToken);

        report.Passed.Should().BeTrue(report.Error);
    }
}
