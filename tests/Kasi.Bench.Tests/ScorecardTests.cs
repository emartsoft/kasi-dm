using Kasi.Bench.Macro;
using Kasi.Bench.Model;

namespace Kasi.Bench.Tests;

[Trait("Requirement", "KASI-NFR-001")]
public sealed class ScorecardTests
{
    private static ResultRow Row(string source, int connections, string tool, int run, double seconds, bool ok = true) =>
        new(source, connections, run, tool, seconds, 1000, 8000 / seconds, connections, ok);

    private static BenchmarkSession Session(params ResultRow[] rows) => new(
        BenchmarkSession.CurrentSchemaVersion,
        DateTimeOffset.UnixEpoch,
        new MachineInfo("cpu", 8, 16000, "os"),
        940,
        "night",
        true,
        new Dictionary<string, string> { ["idm"] = "6.42", ["kasi"] = "bench" },
        rows);

    private static IEnumerable<ResultRow> Both(string source, int connections, double kasi, double idm, int runs = 5) =>
        Enumerable.Range(1, runs).SelectMany(run => new[] { Row(source, connections, "idm", run, idm), Row(source, connections, "kasi", run, kasi) });

    [Fact]
    public void The_ratio_is_the_median_kasi_time_over_the_median_idm_time_and_one_slow_run_does_not_decide_it()
    {
        var rows = new[]
        {
            Row("a", 8, "kasi", 1, 10), Row("a", 8, "kasi", 2, 11), Row("a", 8, "kasi", 3, 60),
            Row("a", 8, "idm", 1, 10), Row("a", 8, "idm", 2, 10), Row("a", 8, "idm", 3, 10),
        };

        var line = Scorecard.Build(Session(rows)).Lines.Should().ContainSingle().Subject;

        line.Ratio.Should().BeApproximately(1.1, 1e-9);
        line.KasiSeconds.Should().Be(11);
        line.IdmSeconds.Should().Be(10);
    }

    [Fact]
    public void The_gate_needs_a_median_at_most_1_10_and_no_source_above_1_25()
    {
        var pass = Scorecard.Build(Session(Both("a", 8, 10.5, 10).Concat(Both("b", 8, 11, 10)).Concat(Both("c", 8, 9, 10)).ToArray()));
        var tooSlowOverall = Scorecard.Build(Session(Both("a", 8, 11.5, 10).Concat(Both("b", 8, 11.5, 10)).Concat(Both("c", 8, 11.5, 10)).ToArray()));
        var oneBadSource = Scorecard.Build(Session(Both("a", 8, 9, 10).Concat(Both("b", 8, 9, 10)).Concat(Both("c", 8, 13, 10)).ToArray()));

        pass.Passed.Should().BeTrue();
        pass.MedianRatioByConnections[8].Should().BeApproximately(1.05, 1e-9);
        tooSlowOverall.Passed.Should().BeFalse("the median 1.15 is over 1.10");
        oneBadSource.Passed.Should().BeFalse("one source at 1.30 is over 1.25 although the median is fine");
        oneBadSource.MedianRatioByConnections[8].Should().BeLessThanOrEqualTo(1.10);
    }

    [Fact]
    public void Each_connection_count_is_judged_on_its_own_and_both_must_pass()
    {
        var rows = Both("a", 8, 10, 10).Concat(Both("a", 16, 12, 10)).ToArray();

        var result = Scorecard.Build(Session(rows));

        result.MedianRatioByConnections[8].Should().Be(1);
        result.MedianRatioByConnections[16].Should().BeApproximately(1.2, 1e-9);
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public void A_source_whose_runs_failed_verification_is_excluded_and_listed_never_counted_as_fast()
    {
        var rows = Both("good", 8, 10, 10)
            .Concat(Enumerable.Range(1, 3).Select(run => Row("broken", 8, "kasi", run, 1, ok: false)))
            .Concat(Enumerable.Range(1, 3).Select(run => Row("broken", 8, "idm", run, 10)))
            .Concat(Enumerable.Range(1, 3).Select(run => Row("idm-missing", 8, "kasi", run, 5)))
            .ToArray();

        var result = Scorecard.Build(Session(rows));

        result.Lines.Select(l => l.Source).Should().Equal("good");
        result.Excluded.Should().HaveCount(2);
        result.Excluded.Should().Contain(e => e.Source == "broken" && e.Reason.Contains("Kasi", StringComparison.Ordinal));
        result.Excluded.Should().Contain(e => e.Source == "idm-missing" && e.Reason.Contains("no runs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_session_with_no_usable_pair_is_no_data_and_never_a_pass()
    {
        var result = Scorecard.Build(Session(Row("a", 8, "kasi", 1, 5, ok: false)));

        result.HasData.Should().BeFalse();
        result.Passed.Should().BeFalse();
        Scorecard.ToMarkdown(result, Session()).Should().Contain("NO DATA");
    }

    [Fact]
    public void The_release_limit_is_tighter_than_the_gate_limit()
    {
        var rows = Both("a", 8, 10.7, 10).ToArray();

        Scorecard.Build(Session(rows)).Passed.Should().BeTrue();
        Scorecard.Build(Session(rows), Scorecard.ReleaseMedianLimit).Passed.Should().BeFalse();
    }

    [Fact]
    public void The_markdown_shows_the_conditions_of_the_session_and_marks_a_source_over_the_limit()
    {
        var session = Session(Both("a", 8, 13, 10).ToArray());

        var text = Scorecard.ToMarkdown(Scorecard.Build(session), session);

        text.Should().Contain("FAIL").And.Contain("window night").And.Contain("940 Mbit/s").And.Contain("DNS flushed between runs: yes").And.Contain("1.300 ✗");
    }
}
