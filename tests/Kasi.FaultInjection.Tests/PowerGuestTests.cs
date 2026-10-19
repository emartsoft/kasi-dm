using System.Diagnostics;
using Kasi.Data;
using Kasi.FaultInjection;
using Microsoft.Data.Sqlite;

namespace Kasi.FaultInjection.Tests;

/// <summary>The guest side of the power-cut suite, with a process kill standing in for the power cut (the Hyper-V run is owner evidence).</summary>
[Trait("Category", "Integration")]
[Trait("TC", "TC-RES-003")]
[Trait("Requirement", "KASI-RES-003")]
public sealed class PowerGuestTests
{
    private const long Size = 32L * 1024 * 1024;
    private const long Rate = 4L * 1024 * 1024;

    [Fact]
    public async Task After_the_guest_dies_mid_download_verify_finds_a_sound_database_and_finishes_the_file()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = Path.Combine(Path.GetTempPath(), "kasi-power-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { FaultHarness.FindChildDll(), "--scenario", "power-guest", "--phase", "run", "--state", state, "--size", "33554432", "--rate", "4194304" })
            {
                start.ArgumentList.Add(arg);
            }

            using (var guest = Process.Start(start)!)
            {
                await WaitForProgressAsync(state, ct);
                guest.Kill(entireProcessTree: true);
                await guest.WaitForExitAsync(ct);
            }

            SqliteConnection.ClearAllPools();
            var exit = await FaultInjectionRunner.RunAsync(
                new FaultInjectionOptions { Scenario = "power-guest", Phase = "verify", State = state, Size = Size, Rate = Rate }, ct);

            exit.Should().Be(0, await File.ReadAllTextAsync(Path.Combine(state, "power-report.json"), ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(state, recursive: true);
            }
            catch (IOException)
            {
                // a handle can outlive the process by a moment
            }
        }
    }

    private static async Task WaitForProgressAsync(string state, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(60))
        {
            var jobFile = Path.Combine(state, "job.id");
            if (File.Exists(jobFile) && Kasi.Core.JobId.TryParse((await File.ReadAllTextAsync(jobFile, ct)).Trim(), out var id))
            {
                try
                {
                    var job = await new JobRepository(new SqliteConnectionFactory(Path.Combine(state, "fault.db"))).GetAsync(id, ct);
                    if (job is { DoneBytes: > 0 })
                    {
                        return;
                    }
                }
                catch (SqliteException)
                {
                    // the guest is still creating the database
                }
            }

            await Task.Delay(100, ct);
        }

        throw new TimeoutException("The guest checkpointed nothing in 60 s.");
    }
}
