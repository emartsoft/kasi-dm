using System.Diagnostics;
using System.Text.Json;

namespace Kasi.ArchitectureTests;

/// <summary>
/// The M1 gate check (plan.md 4.2, M1.16c): <c>build/Check-M1Gate.ps1</c> judges evidence it is handed, so these tests prepare
/// evidence files and check the verdict: three green nights, a skipped or failed suite, a missing benchmark, low coverage, an open
/// High defect and a power-cut case nobody has recorded.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-NFR-018")]
public sealed class GateCheckScriptTests
{
    private static readonly string _script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "build", "Check-M1Gate.ps1"));
    private static readonly string[] _suites = ["kill", "commit-kill", "server-mutation", "storage-stall", "disk-pressure", "network-soak"];

    private static object[] Cases() =>
        [.. Enumerable.Range(1, 24).Select(n => $"TC-ENG-{n:D3}"), .. Enumerable.Range(1, 12).Select(n => $"TC-RES-{n:D3}")];

    private static object Night(int id, string conclusion = "success", string? failing = null, string verdict = "PASS") => new
    {
        id,
        url = $"https://example.test/runs/{id}",
        conclusion,
        suites = _suites.Select(s => new { name = s, verdict = s == failing ? verdict : "PASS" }).ToArray(),
    };

    private static Dictionary<string, object?> ReadyEvidence() => new()
    {
        ["build"] = "CI run 1 at abc1234",
        ["links"] = new { ci = "https://example.test/ci", nights = new[] { "https://example.test/runs/1" } },
        ["testCases"] = new
        {
            required = Cases(),
            ownerRequired = new[] { "TC-RES-003" },
            tagged = Cases(),
            ownerEvidence = new Dictionary<string, object> { ["TC-RES-003"] = new { date = "2026-11-02", iterations = 20 } },
            ciGreen = true,
        },
        ["nights"] = new[] { Night(3), Night(2), Night(1) },
        ["benchmark"] = new { file = "idm-comparison-2026-11-01.json", passed = true },
        ["coverage"] = 0.865,
        ["openDefects"] = 0,
    };

    private static async Task<(int Exit, string Output)> JudgeAsync(Dictionary<string, object?> evidence)
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "evidence.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(evidence), TestContext.Current.CancellationToken);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment.Remove("GITHUB_STEP_SUMMARY");
            foreach (var argument in new[] { "-NoProfile", "-File", _script, "-EvidenceFile", file, "-OutputDir", folder })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            var report = File.Exists(Path.Combine(folder, "gate-report.md")) ? await File.ReadAllTextAsync(Path.Combine(folder, "gate-report.md"), TestContext.Current.CancellationToken) : string.Empty;
            return (process.ExitCode, await output + await error + report);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task With_every_piece_of_evidence_the_gate_is_ready_for_sign_off()
    {
        var (exit, output) = await JudgeAsync(ReadyEvidence());

        exit.Should().Be(0, output);
        output.Should().Contain("READY FOR SIGN-OFF").And.Contain("CI run 1 at abc1234");
    }

    [Fact]
    public async Task One_red_night_among_three_keeps_the_gate_open()
    {
        var evidence = ReadyEvidence();
        evidence["nights"] = new[] { Night(3), Night(2, conclusion: "failure"), Night(1) };

        var (exit, output) = await JudgeAsync(evidence);

        exit.Should().Be(1);
        output.Should().Contain("NOT READY").And.Contain("run 2 concluded failure");
    }

    [Fact]
    public async Task A_green_night_with_a_skipped_suite_is_not_a_pass()
    {
        var evidence = ReadyEvidence();
        evidence["nights"] = new[] { Night(3), Night(2, failing: "disk-pressure", verdict: "SKIPPED"), Night(1) };

        var (exit, output) = await JudgeAsync(evidence);

        exit.Should().Be(1);
        output.Should().Contain("disk-pressure is SKIPPED");
    }

    [Fact]
    public async Task Fewer_than_three_scheduled_nights_keep_the_gate_open()
    {
        var evidence = ReadyEvidence();
        evidence["nights"] = new[] { Night(2), Night(1) };

        var (exit, output) = await JudgeAsync(evidence);

        exit.Should().Be(1);
        output.Should().Contain("only 2 scheduled night(s)");
    }

    [Fact]
    public async Task A_missing_benchmark_session_is_open_owner_evidence()
    {
        var evidence = ReadyEvidence();
        evidence["benchmark"] = null;

        var (exit, output) = await JudgeAsync(evidence);

        exit.Should().Be(1);
        output.Should().Contain("no idm-comparison session yet");
    }

    [Fact]
    public async Task Coverage_below_eighty_percent_an_open_high_defect_and_an_unrecorded_power_cut_each_keep_the_gate_open()
    {
        var lowCoverage = ReadyEvidence();
        lowCoverage["coverage"] = 0.79;
        var defect = ReadyEvidence();
        defect["openDefects"] = 1;
        var noPowerCut = ReadyEvidence();
        noPowerCut["testCases"] = new
        {
            required = Cases(),
            ownerRequired = new[] { "TC-RES-003" },
            tagged = Cases(),
            ownerEvidence = new Dictionary<string, object>(),
            ciGreen = true,
        };

        (await JudgeAsync(lowCoverage)).Output.Should().Contain("79.0");
        (await JudgeAsync(defect)).Output.Should().Contain("1 open Critical or High defect");
        (await JudgeAsync(noPowerCut)).Output.Should().Contain("TC-RES-003");
        foreach (var evidence in new[] { lowCoverage, defect, noPowerCut })
        {
            (await JudgeAsync(evidence)).Exit.Should().Be(1);
        }
    }

    [Fact]
    public async Task A_case_with_no_tagged_test_and_a_red_ci_run_are_reported()
    {
        var evidence = ReadyEvidence();
        evidence["testCases"] = new
        {
            required = Cases(),
            ownerRequired = new[] { "TC-RES-003" },
            tagged = Cases().Where(c => (string)c != "TC-ENG-017").ToArray(),
            ownerEvidence = new Dictionary<string, object> { ["TC-RES-003"] = true },
            ciGreen = false,
        };

        var (exit, output) = await JudgeAsync(evidence);

        exit.Should().Be(1);
        output.Should().Contain("TC-ENG-017");
    }
}
