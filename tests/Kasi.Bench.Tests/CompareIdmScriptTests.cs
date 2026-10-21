using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Kasi.Bench.Macro;

namespace Kasi.Bench.Tests;

/// <summary>
/// A dry run of <c>bench/Compare-Idm.ps1</c> (M1.15b) against a local Kasi.TestServer, with a fake <c>IDMan</c> that, like the real one,
/// only queues the download and returns. It proves the alternating loop, the wait for the file, the verification and the scorecard;
/// the numbers mean nothing.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-NFR-001")]
public sealed class CompareIdmScriptTests
{
    private const long Size = 4L * 1024 * 1024;

    [Fact]
    public async Task The_alternating_dry_run_times_both_tools_verifies_the_files_and_writes_a_session_and_a_scorecard()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = RepositoryRoot();
        var folder = Path.Combine(Path.GetTempPath(), "kasi-idm-dry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        await using var server = await ServerProcess.StartAsync(ct);
        try
        {
            var url = new Uri(server.BaseAddress, $"files/dry-run/{Size}").AbsoluteUri;
            var sources = Path.Combine(folder, "sources.json");
            await File.WriteAllTextAsync(
                sources,
                JsonSerializer.Serialize(new
                {
                    sources = new[] { new { id = "dry", type = "t", url, sizeBytes = Size, purpose = "p", owner = "o", placeholder = false } },
                    local = Array.Empty<object>(),
                }),
                ct);

            // like IDMan /d <url> /p <folder> /f <name> /n /q: queue the download in the background and return at once
            var fakeIdm = Path.Combine(folder, "IDMan.cmd");
            await File.WriteAllTextAsync(
                fakeIdm,
                "@echo off\r\nstart \"\" /b powershell -NoProfile -Command \"Start-Sleep -Milliseconds 700; Invoke-WebRequest -Uri '%~2' -OutFile '%~4\\%~6' -UseBasicParsing\"\r\nexit /b 0\r\n",
                ct);

            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[]
            {
                "-NoProfile", "-File", Path.Combine(root, "bench", "Compare-Idm.ps1"),
                "-Sources", sources, "-Connections", "4", "-Runs", "2", "-Window", "night", "-LineSpeedMbps", "940",
                "-OutputDir", folder, "-IdmPath", fakeIdm, "-KasiBench", FindBenchDll(root), "-WorkDir", Path.Combine(folder, "work"),
                "-SkipDnsFlush", "-NoPrompt",
            })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(3), ct);
            var log = await output + await error;

            // the fake is slower than the engine, so the gate passes; what matters is that every row is there and verified
            process.ExitCode.Should().Be(0, log);
            var session = Directory.GetFiles(folder, "idm-comparison-*.json").Should().ContainSingle().Subject;
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(session, ct));
            var rows = document.RootElement.GetProperty("rows").EnumerateArray().ToList();
            rows.Should().HaveCount(4, "two runs of each of the two tools");
            rows.Select(r => r.GetProperty("tool").GetString()).Should().Equal("idm", "kasi", "idm", "kasi");
            rows.Should().OnlyContain(r => r.GetProperty("sha256Ok").GetBoolean() && r.GetProperty("bytes").GetInt64() == Size);
            document.RootElement.GetProperty("dnsFlushed").GetBoolean().Should().BeFalse("the dry run skipped the flush and says so");
            document.RootElement.GetProperty("window").GetString().Should().Be("night");
            Directory.GetFiles(folder, "idm-comparison-*.md").Should().ContainSingle();
            log.Should().Contain("Kasi vs IDM: PASS");
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // a background download of the fake can outlive the run by a moment
            }
        }
    }

    [Fact]
    public async Task With_only_placeholder_sources_the_script_stops_instead_of_pretending()
    {
        var ct = TestContext.Current.CancellationToken;
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        var fake = Path.Combine(Path.GetTempPath(), "IDMan-" + Guid.NewGuid().ToString("N") + ".cmd");
        await File.WriteAllTextAsync(fake, "@echo off\r\n", ct);
        try
        {
            foreach (var argument in new[] { "-NoProfile", "-File", Path.Combine(RepositoryRoot(), "bench", "Compare-Idm.ps1"), "-IdmPath", fake, "-KasiBench", FindBenchDll(RepositoryRoot()), "-NoPrompt", "-SkipDnsFlush" })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            process.ExitCode.Should().NotBe(0);
            (await output + await error).Should().Contain("No runnable source");
        }
        finally
        {
            File.Delete(fake);
        }
    }

    private static string RepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static string FindBenchDll(string root) =>
        Directory.GetFiles(Path.Combine(root, "bench", "Kasi.Bench", "bin"), "Kasi.Bench.dll", SearchOption.AllDirectories)
            .Where(path => File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .First();
}
