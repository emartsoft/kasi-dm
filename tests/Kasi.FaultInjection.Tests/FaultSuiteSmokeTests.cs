using Kasi.FaultInjection;

namespace Kasi.FaultInjection.Tests;

/// <summary>Small runs of the mutation, storage-stall and disk-pressure suites through the same code the nightly job runs.</summary>
[Trait("Category", "Integration")]
public sealed class FaultSuiteSmokeTests
{
    [Trait("TC", "TC-RES-005")]
    [Trait("TC", "TC-RES-006")]
    [Trait("Requirement", "KASI-RES-005")]
    [Fact]
    public async Task A_file_that_changes_between_or_during_sessions_is_restarted_and_finishes_as_one_whole_version()
    {
        var options = new FaultInjectionOptions { Scenario = "server-mutation", Seed = 21, Iterations = 4, Size = 32L * 1024 * 1024, Rate = 4L * 1024 * 1024 };

        var report = await FaultInjectionRunner.RunToReportAsync(options, TestContext.Current.CancellationToken);

        report.Passed.Should().BeTrue(report.Error);
        report.Completed.Should().Be(4);
    }

    [Trait("TC", "TC-ENG-015")]
    [Trait("Requirement", "KASI-NFR-007")]
    [Fact]
    public async Task A_stalled_disk_is_not_mistaken_for_a_dead_connection()
    {
        var options = new FaultInjectionOptions { Scenario = "storage-stall", Seed = 22, Iterations = 1, Size = 16L * 1024 * 1024, Rate = 8L * 1024 * 1024 };

        var report = await FaultInjectionRunner.RunToReportAsync(options, TestContext.Current.CancellationToken);

        report.Passed.Should().BeTrue(report.Error);
        report.MaxRefetchedBytes.Should().Be(0);
    }

    [Trait("TC", "TC-ENG-015")]
    [Trait("Requirement", "KASI-ENG-008")]
    [Fact]
    public async Task A_full_volume_pauses_the_job_and_freeing_space_lets_it_finish()
    {
        var options = new FaultInjectionOptions
        {
            Scenario = "disk-pressure",
            Seed = 23,
            Iterations = 2,
            Size = 64L * 1024 * 1024,
            Rate = 64L * 1024 * 1024,
            VhdMebibytes = 256,
            FreeAfterSeconds = 1,
        };

        var report = await FaultInjectionRunner.RunToReportAsync(options, TestContext.Current.CancellationToken);

        if (report.Skipped)
        {
            Assert.Skip(report.SkipReason!);
        }

        report.Passed.Should().BeTrue(report.Error);
    }
}
