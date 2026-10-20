using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Kasi.Core;
using Kasi.Engine;
using Kasi.TestServer;
using Microsoft.Data.Sqlite;

namespace Kasi.FaultInjection;

/// <summary>
/// The disk-pressure suite (TPL 7.1, TC-ENG-015 resilience). The download goes to a small NTFS volume (a VHDX mounted in a
/// folder) that has less free space than the file needs. The job must pause with <c>NotEnoughSpace</c> without hurting the
/// database; after the space is freed (default 30 s later) a resume must finish the file with the right SHA-256.
/// Mounting a VHDX needs Administrator rights; without them the suite reports itself as not run.
/// </summary>
internal static class DiskPressureSuite
{
    public static async Task<FaultInjectionReport> RunAsync(FaultHarness harness, FaultInjectionOptions options, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || !IsAdministrator())
        {
            return FaultInjectionReport.NotRun(options.Scenario, options.Seed, "mounting a VHDX needs Administrator rights on Windows; run this suite elevated");
        }

        var started = Stopwatch.StartNew();
        var vhd = Path.Combine(harness.Root, "pressure.vhdx");
        var mount = Path.Combine(harness.Root, "volume");
        Directory.CreateDirectory(mount);
        var results = new List<FaultIterationResult>();
        var verified = 0;
        string? error = null;
        string? lastSha = null;

        try
        {
            var (exitCode, output) = await DiskPartAsync(
                [
                    $"create vdisk file=\"{vhd}\" maximum={options.VhdMebibytes} type=expandable",
                $"select vdisk file=\"{vhd}\"",
                "attach vdisk",
                "create partition primary",
                "format fs=ntfs label=\"KasiPressure\" quick",
                $"assign mount=\"{mount}\"",
            ],
                cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                return new FaultInjectionReport(options.Scenario, options.Seed, 0, 0, 0, 1, 0, 0, false, null, started.Elapsed, $"diskpart could not mount the volume (exit {exitCode}): {Tail(output)}", []);
            }

            {
                var folder = Path.Combine(mount, "downloads");
                var filler = Path.Combine(mount, "filler.bin");
                for (var i = 0; i < options.IterationCount && error is null; i++)
                {
                    var seed = $"fault-pressure-{options.Seed}-{i}";
                    var problem = await RunOneAsync(harness, options, seed, folder, filler, cancellationToken).ConfigureAwait(false);
                    if (problem.Error is null)
                    {
                        verified++;
                        lastSha = problem.Sha;
                    }

                    results.Add(new FaultIterationResult(i, problem.Error is null, problem.Error, 0, 0, 0));
                    error ??= problem.Error;
                }
            }
        }
        finally
        {
            await DiskPartAsync([$"select vdisk file=\"{vhd}\"", "detach vdisk noerr"], CancellationToken.None).ConfigureAwait(false);
        }

        return new FaultInjectionReport(
            options.Scenario,
            options.Seed,
            results.Count,
            0,
            verified,
            results.Count(r => !r.Success),
            harness.Host.Metrics.BytesServed,
            0,
            error is null && verified == options.IterationCount,
            lastSha,
            started.Elapsed,
            error,
            results);
    }

    private static async Task<(string? Error, string? Sha)> RunOneAsync(
        FaultHarness harness,
        FaultInjectionOptions options,
        string seed,
        string folder,
        string filler,
        CancellationToken cancellationToken)
    {
        // leave half of what the file needs
        if (!WindowsDiskSpaceProvider.Instance.TryGetAvailableFreeBytes(Path.GetDirectoryName(filler)!, out var free))
        {
            return ("the free space of the volume cannot be read", null);
        }

        var fillBytes = free - (options.DownloadSize / 2);
        if (fillBytes <= 0)
        {
            return ($"the volume ({options.VhdMebibytes} MiB) is too small for a {options.DownloadSize} B download; raise --vhd-mib or lower --size", null);
        }

        await using (var fill = new FileStream(filler, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fill.SetLength(fillBytes);
        }

        var job = harness.NewJob($"/files/{seed}/{options.DownloadSize}", $"{seed}.bin", options.ConnectionCount, folder);
        await harness.Jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
        var limit = TimeSpan.FromSeconds((options.DownloadSize / options.BytesPerSecond * 3) + 120);

        using (var first = harness.StartChild(job.Id, options.BytesPerSecond))
        {
            if (!await FaultHarness.WaitOrTimeoutAsync(first, limit, cancellationToken).ConfigureAwait(false))
            {
                FaultHarness.KillIfRunning(first);
                return ("the first child did not stop on a full disk", null);
            }
        }

        SqliteConnection.ClearAllPools();
        var paused = (await harness.GetJobAsync(job.Id, cancellationToken).ConfigureAwait(false))!;
        if (paused is not { State: JobState.Paused, PauseReason: PauseReason.NotEnoughSpace })
        {
            return ($"on a full disk the job was {paused.State} ({paused.PauseReason}), not Paused with NotEnoughSpace", null);
        }

        var damaged = await FaultInjectionRunner.CheckDatabaseAsync(harness.DatabasePath, job.Id, cancellationToken).ConfigureAwait(false);
        if (damaged is not null)
        {
            return ($"the database was damaged by the full disk: {damaged}", null);
        }

        await Task.Delay(TimeSpan.FromSeconds(options.FreeAfterSeconds), cancellationToken).ConfigureAwait(false);
        File.Delete(filler);

        using (var second = harness.StartChild(job.Id, options.BytesPerSecond))
        {
            if (!await FaultHarness.WaitOrTimeoutAsync(second, limit, cancellationToken).ConfigureAwait(false) || second.ExitCode != 0)
            {
                var exit = second.HasExited ? second.ExitCode : -1;
                FaultHarness.KillIfRunning(second);
                return ($"after space was freed the resumed child did not complete (exit {exit})", null);
            }
        }

        var path = Path.Combine(folder, job.FileName);
        var sha = await FaultHarness.Sha256Async(path, cancellationToken).ConfigureAwait(false);
        File.Delete(path);
        return sha == GeneratedContent.Sha256Hex(seed, options.DownloadSize)
            ? (null, sha)
            : ("the finished file does not match the generated content", null);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string Tail(string text) => text.Length <= 300 ? text : text[^300..];

    private static async Task<(int ExitCode, string Output)> DiskPartAsync(IReadOnlyList<string> commands, CancellationToken cancellationToken)
    {
        var script = Path.Combine(Path.GetTempPath(), $"kasi-diskpart-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(script, commands, cancellationToken).ConfigureAwait(false);
            using var process = Process.Start(new ProcessStartInfo("diskpart.exe", $"/s \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return (process.ExitCode, $"{await stdout.ConfigureAwait(false)}\n{await stderr.ConfigureAwait(false)}".Trim());
        }
        finally
        {
            File.Delete(script);
        }
    }
}
