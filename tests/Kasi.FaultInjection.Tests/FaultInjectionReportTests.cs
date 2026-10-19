using System.Text.Json;
using Kasi.FaultInjection;

namespace Kasi.FaultInjection.Tests;

public sealed class FaultInjectionReportTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static FaultInjectionReport Sample(string? error = null, bool ok = true) => new(
        "kill",
        7,
        3,
        3,
        1,
        ok ? 0 : 1,
        12345,
        4096,
        ok,
        "abc",
        TimeSpan.FromSeconds(2),
        error,
        [new FaultIterationResult(0, ok, ok ? null : "integrity_check returned 'bad'", 12345, 4096, 1)]);

    [Fact]
    public async Task The_json_report_and_summary_are_written_together_and_carry_the_seed()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fault-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Sample().WriteAsync(Path.Combine(folder, "fault-report.json"), TestContext.Current.CancellationToken);

            var json = await File.ReadAllTextAsync(Path.Combine(folder, "fault-report.json"), TestContext.Current.CancellationToken);
            var loaded = JsonSerializer.Deserialize<FaultInjectionReport>(json, _json)!;
            loaded.Seed.Should().Be(7);
            loaded.MaxRefetchedBytes.Should().Be(4096);
            loaded.Results.Should().ContainSingle();
            (await File.ReadAllTextAsync(Path.Combine(folder, "summary.md"), TestContext.Current.CancellationToken))
                .Should().Contain("PASS").And.Contain("--seed 7");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_failed_report_names_the_failing_iteration_and_how_to_re_run_it()
    {
        var report = Sample("integrity_check returned 'bad'", ok: false);

        report.Passed.Should().BeFalse();
        report.ToMarkdown().Should().Contain("FAIL").And.Contain("iteration 0: integrity_check returned 'bad'").And.Contain("--scenario kill --seed 7");
    }

    [Fact]
    public void A_suite_that_could_not_run_is_reported_as_skipped_and_is_not_a_pass()
    {
        var report = FaultInjectionReport.NotRun("disk-pressure", 5, "needs Administrator rights");

        report.Skipped.Should().BeTrue();
        report.Passed.Should().BeFalse();
        report.ToMarkdown().Should().Contain("SKIPPED").And.Contain("needs Administrator rights");
    }
}
