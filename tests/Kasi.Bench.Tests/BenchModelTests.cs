using Kasi.Bench;
using Kasi.Bench.Macro;
using Kasi.Bench.Model;

namespace Kasi.Bench.Tests;

[Trait("Requirement", "KASI-NFR-001")]
public sealed class BenchModelTests
{
    private static BenchmarkSession Session(params ResultRow[] rows) => new(
        BenchmarkSession.CurrentSchemaVersion,
        DateTimeOffset.UnixEpoch,
        new MachineInfo("cpu", 8, 16000, "os"),
        940,
        "night",
        true,
        new Dictionary<string, string> { ["kasi"] = "test" },
        rows);

    private static ResultRow Row(string source, int run, double mbps, double? memory = null, string tool = "kasi", bool ok = true) =>
        new(source, 16, run, tool, 10, 1000, mbps, 16, ok, memory);

    [Fact]
    public async Task The_shipped_sources_file_loads_and_every_archetype_is_a_marked_placeholder_until_the_owner_fills_it_in()
    {
        var file = await BenchmarkSourceFile.LoadAsync(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "sources.json"),
            TestContext.Current.CancellationToken);

        file.Sources.Should().HaveCount(6);
        file.Runnable.Should().BeEmpty("nothing is fetched until the owner replaces the placeholders");
        file.Local.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("https://host.example/file.bin?token=abc")]
    [InlineData("https://user:pw@host.example/file.bin")]
    [InlineData("ftp://host.example/file.bin")]
    [InlineData("not a url")]
    public void A_source_url_with_a_query_credentials_or_another_scheme_is_rejected(string url)
    {
        var file = new BenchmarkSourceFile([new BenchmarkSource("a", "t", url, null, "p", "o", false)], []);

        var act = file.Validate;

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Repeated_ids_are_rejected()
    {
        var one = new BenchmarkSource("a", "t", "https://h.example/f", null, "p", "o", false);

        var act = new BenchmarkSourceFile([one, one], []).Validate;

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task A_session_round_trips_through_its_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "bench-session-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var session = Session(Row("s", 1, 900, 120));
            await session.SaveAsync(path, TestContext.Current.CancellationToken);

            var loaded = await BenchmarkSession.LoadAsync(path, TestContext.Current.CancellationToken);

            loaded.Window.Should().Be("night");
            loaded.DnsFlushed.Should().BeTrue();
            loaded.LineSpeedMbps.Should().Be(940);
            loaded.Rows.Should().ContainSingle().Which.PeakWorkingSetMegabytes.Should().Be(120);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_throughput_drop_over_five_percent_is_flagged_and_one_under_is_not()
    {
        var before = Session(Row("s", 1, 1000), Row("s", 2, 1000), Row("s", 3, 1000));

        var worse = ResultComparer.Compare(before, Session(Row("s", 1, 940), Row("s", 2, 940), Row("s", 3, 940)));
        var fine = ResultComparer.Compare(before, Session(Row("s", 1, 960), Row("s", 2, 960), Row("s", 3, 960)));

        worse.Should().ContainSingle().Which.Flagged.Should().BeTrue();
        worse[0].Reason.Should().Contain("throughput fell");
        fine.Should().ContainSingle().Which.Flagged.Should().BeFalse();
    }

    [Fact]
    public void A_memory_increase_over_ten_percent_is_flagged()
    {
        var before = Session(Row("s", 1, 1000, 100));

        var grew = ResultComparer.Compare(before, Session(Row("s", 1, 1000, 111)));
        var steady = ResultComparer.Compare(before, Session(Row("s", 1, 1000, 109)));

        grew.Should().ContainSingle().Which.Flagged.Should().BeTrue();
        steady.Should().ContainSingle().Which.Flagged.Should().BeFalse();
    }

    [Fact]
    public void The_comparison_uses_the_median_so_one_slow_run_does_not_flag_and_failed_runs_are_ignored()
    {
        var before = Session(Row("s", 1, 1000), Row("s", 2, 1000), Row("s", 3, 1000));
        var now = Session(Row("s", 1, 995), Row("s", 2, 400), Row("s", 3, 1005), Row("s", 4, 10, ok: false));

        ResultComparer.Compare(before, now).Should().ContainSingle().Which.Flagged.Should().BeFalse();
    }

    [Theory]
    [InlineData(1200, 1100, ThroughputVerdict.Pass)]
    [InlineData(1200, 900, ThroughputVerdict.Fail)]
    [InlineData(800, 790, ThroughputVerdict.Inconclusive)]
    [InlineData(999, 2000, ThroughputVerdict.Inconclusive)]
    public void The_one_gigabit_verdict_is_inconclusive_when_the_control_cannot_reach_it_itself(double control, double engine, ThroughputVerdict expected) =>
        Local1GbitRunner.Evaluate(control, engine).Should().Be(expected);

    [Fact]
    public void Megabits_and_cpu_percent_are_computed_from_bytes_and_time()
    {
        EngineDownload.Megabits(125_000_000, 1).Should().Be(1000);
        EngineDownload.Megabits(1, 0).Should().Be(0);
        ResourceSampler.CpuPercent(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(20)).Should().Be(5);
    }

    [Fact]
    public void The_macro_mode_arguments_parse_as_name_value_pairs_and_flags()
    {
        var options = Cli.Parse(["--sources", "a.json", "--local", "--runs", "3"]);

        options["sources"].Should().Be("a.json");
        options.Should().ContainKey("local");
        Cli.Ints("8, 16").Should().Equal(8, 16);
    }

    [Fact]
    public void The_seed_is_taken_from_a_files_route()
    {
        ThroughputRunner.SeedOf("/files/bench-local/1024").Should().Be("bench-local");
        ThroughputRunner.SeedOf("/mutable/x").Should().BeNull();
    }
}
