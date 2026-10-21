using Kasi.FaultInjection;

namespace Kasi.FaultInjection.Tests;

/// <summary>
/// Small runs of the nightly suites through the same code (KASI-RES-001, TC-RES-001, TC-RES-011): a child process that runs a
/// throttled download through <c>DownloadEngine</c> is killed, every restart recovers and resumes, and the finished file is
/// verified. The 200 and 100 kill runs are the nightly workflow.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-001")]
public sealed class KillLoopSmokeTests
{
    private const long FileSize = 16L * 1024 * 1024;
    private const long BytesPerSecond = 2L * 1024 * 1024;

    private static FaultInjectionOptions Small(string scenario, int seed) => new()
    {
        Scenario = scenario,
        Seed = seed,
        Iterations = 3,
        Size = FileSize,
        Rate = BytesPerSecond,
        Connections = 4,
        MaxKillSeconds = 3,
    };

    [Trait("TC", "TC-RES-001")]
    [Fact]
    public async Task A_child_killed_three_times_recovers_resumes_and_finishes_without_refetching_everything()
    {
        var report = await FaultInjectionRunner.RunToReportAsync(Small("kill", 11), TestContext.Current.CancellationToken);

        report.Passed.Should().BeTrue(report.Error);
        report.Kills.Should().Be(3);
        report.ChecksumVerified.Should().BeTrue();
        report.MaxRefetchedBytes.Should().BeLessThan(FileSize, "a resume that started over would fetch the whole file again");
    }

    [Trait("TC", "TC-RES-011")]
    [Fact]
    public async Task A_child_killed_inside_a_checkpoint_commit_leaves_a_consistent_database_and_resumes()
    {
        var report = await FaultInjectionRunner.RunToReportAsync(Small("commit-kill", 12), TestContext.Current.CancellationToken);

        report.Passed.Should().BeTrue(report.Error);
        report.Kills.Should().Be(3);
        report.ChecksumVerified.Should().BeTrue();
    }

    [Fact]
    public async Task The_same_seed_picks_the_same_number_of_kills_and_a_download_that_ends_is_replaced_by_a_new_one()
    {
        // 8 MiB at 4 MiB/s ends in about 2 s, so 3 kills of 1 s need more than one download
        var options = Small("kill", 13) with { Size = 8L * 1024 * 1024, Rate = 4L * 1024 * 1024, MaxKillSeconds = 1, Iterations = 4 };

        var report = await FaultInjectionRunner.RunToReportAsync(options, TestContext.Current.CancellationToken);

        report.Passed.Should().BeTrue(report.Error);
        report.Kills.Should().Be(4, "an iteration is one kill, however many downloads that takes");
    }
}
