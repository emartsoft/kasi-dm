using System.Diagnostics;

namespace Kasi.ArchitectureTests;

/// <summary>
/// The engine coverage gate (KASI-NFR-018, M1.16a): <c>build/Test-CoverageGate.ps1</c> must pass at or above the threshold, fail below
/// it, and refuse to pass when its input is missing, so a broken report can never switch the gate off.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-NFR-018")]
public sealed class CoverageGateScriptTests
{
    private static readonly string _script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "Test-CoverageGate.ps1"));

    private static string Cobertura(string engineRate) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coverage line-rate="0.9" version="1.9" timestamp="1">
          <sources />
          <packages>
            <package name="Kasi.Core" line-rate="1" branch-rate="1" complexity="1" />
            <package name="Kasi.Engine" line-rate="{engineRate}" branch-rate="0.7" complexity="1" />
          </packages>
        </coverage>
        """;

    private static async Task<(int Exit, string Output)> RunAsync(string? xml, string assembly = "Kasi.Engine", double threshold = 0.80)
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "Cobertura.xml");
            if (xml is not null)
            {
                await File.WriteAllTextAsync(path, xml, TestContext.Current.CancellationToken);
            }

            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            // the runner's job summary belongs to the real gate step, not to these test runs
            start.Environment.Remove("GITHUB_STEP_SUMMARY");
            foreach (var argument in new[] { "-NoProfile", "-File", _script, "-Cobertura", path, "-Assembly", assembly, "-Threshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            return (process.ExitCode, await output + await error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("0.86", 0)]
    [InlineData("0.80", 0)]
    [InlineData("0.7999", 1)]
    [InlineData("0.42", 1)]
    public async Task The_gate_passes_at_the_threshold_and_fails_below_it(string rate, int expectedExit)
    {
        var (exit, output) = await RunAsync(Cobertura(rate));

        exit.Should().Be(expectedExit, output);
        output.Should().Contain("Kasi.Engine line coverage");
    }

    [Fact]
    public async Task A_missing_file_or_a_missing_assembly_is_an_error_not_a_pass()
    {
        (await RunAsync(null)).Exit.Should().Be(2);
        (await RunAsync(Cobertura("0.99"), assembly: "Kasi.Nope")).Exit.Should().Be(2);
    }

    [Fact]
    public async Task The_threshold_is_an_input_so_raising_it_flips_the_verdict()
    {
        (await RunAsync(Cobertura("0.86"), threshold: 0.80)).Exit.Should().Be(0);
        (await RunAsync(Cobertura("0.86"), threshold: 0.90)).Exit.Should().Be(1);
    }
}
