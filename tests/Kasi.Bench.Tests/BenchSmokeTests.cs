using Kasi.Bench;
using Kasi.Bench.Model;

namespace Kasi.Bench.Tests;

/// <summary>
/// The macro modes end to end against a Kasi.TestServer process. The numbers are not judged here: a shared CI runner says nothing
/// about 1 Gbit/s, so these check the pipeline and the SHA-256.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-NFR-002")]
public sealed class BenchSmokeTests
{
    [Fact]
    public async Task The_one_gigabit_mode_downloads_with_the_engine_verifies_the_hash_and_writes_a_session_file()
    {
        var output = Path.Combine(Path.GetTempPath(), "bench-smoke-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var exit = await Cli.RunAsync(["local-1gbit", "--size", "67108864", "--connections", "4", "--runs", "1", "--out", output], TestContext.Current.CancellationToken);

            // 1 means a failed hash or a failed verdict; on a slow machine the control is below target and the verdict is inconclusive (0)
            exit.Should().Be(0);
            var session = await BenchmarkSession.LoadAsync(output, TestContext.Current.CancellationToken);
            session.Rows.Should().ContainSingle().Which.Sha256Ok.Should().BeTrue();
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task The_throughput_mode_runs_the_local_sources_and_skips_the_placeholders()
    {
        var sources = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bench", "sources.json");
        var output = Path.Combine(Path.GetTempPath(), "bench-throughput-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var exit = await Cli.RunAsync(["throughput", "--sources", sources, "--connections", "4", "--runs", "1", "--local", "--out", output], TestContext.Current.CancellationToken);

            exit.Should().Be(0);
            var session = await BenchmarkSession.LoadAsync(output, TestContext.Current.CancellationToken);
            session.Rows.Select(r => r.Source).Should().BeEquivalentTo("local-files", "local-norange");
            session.Rows.Should().OnlyContain(r => r.Sha256Ok);
        }
        finally
        {
            File.Delete(output);
        }
    }
}
