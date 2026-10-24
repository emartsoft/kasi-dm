using System.Diagnostics;

namespace Kasi.Media.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-004")]
[Trait("TC", "TC-MED-004")]
public sealed class FfmpegRunnerTests
{
    [Fact]
    public async Task A_missing_binary_is_an_unavailable_error_without_a_full_path()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "ffmpeg.exe");
        var runner = new FfmpegRunner(new FfmpegOptions(missing, missing));

        Func<Task> run = () => runner.RunAsync(["-version"], cancellationToken: TestContext.Current.CancellationToken);

        var failure = (await run.Should().ThrowAsync<FfmpegException>()).Which;
        failure.Kind.Should().Be(FfmpegErrorKind.Unavailable);
        failure.Message.Should().NotContain(Path.GetTempPath());
    }

    [Fact]
    public async Task Ffmpeg_stderr_is_redacted_before_it_reaches_the_job_log()
    {
        if (!FfmpegLocator.TryFind(out var options))
        {
            return; // the checked-in muxable tests explain the binary requirement
        }

        var directory = Path.Combine(Path.GetTempPath(), "kasi-ffmpeg-redact", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var missing = Path.Combine(directory, "missing.mp4");
        try
        {
            var runner = new FfmpegRunner(options);
            var result = await runner.RunAsync(["-i", missing], [missing], TestContext.Current.CancellationToken);

            result.ExitCode.Should().NotBe(0);
            result.StandardError.Should().NotContain(directory);
            result.StandardError.Should().Contain("missing.mp4");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Closing_the_job_object_terminates_the_child()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
            },
        };
        process.Start().Should().BeTrue();
        try
        {
            using var job = FfmpegJob.Assign(process);
            job.Dispose();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(timeout.Token);
            process.HasExited.Should().BeTrue();
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
