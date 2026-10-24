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
    public void Every_input_gets_its_own_protocol_whitelist_and_forced_format()
    {
        var video = new MediaJoinedTrack(0, "video", @"C:\w\0.bin", 1, null, "mpegts");
        var audio = new MediaJoinedTrack(1, "audio", @"C:\w\1.bin", 1, null, "mp4");

        var arguments = MediaMuxer.BuildArguments(video, audio, "mp4", @"C:\w\out.part.mp4");

        arguments.Should().ContainInOrder("-protocol_whitelist", "file", "-f", "mpegts", "-i", @"C:\w\0.bin");
        arguments.Should().ContainInOrder("-protocol_whitelist", "file", "-f", "mp4", "-i", @"C:\w\1.bin");
        arguments.Count(argument => argument == "-protocol_whitelist").Should().Be(arguments.Count(argument => argument == "-i"));
    }

    [Fact]
    public async Task A_process_that_prints_too_much_is_truncated_not_buffered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = Path.Combine(Path.GetTempPath(), "kasi-big-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(file, new string('x', 6 * 1024 * 1024), TestContext.Current.CancellationToken);
        try
        {
            var runner = new FfmpegRunner(new FfmpegOptions("cmd.exe", "cmd.exe"));

            var result = await runner.RunProcessAsync("cmd.exe", ["/c", "type", file], null, TestContext.Current.CancellationToken);

            result.ExitCode.Should().Be(0);
            result.StandardOutput.Length.Should().BeLessThanOrEqualTo(4 * 1024 * 1024);
        }
        finally
        {
            File.Delete(file);
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
