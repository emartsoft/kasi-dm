using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kasi.Core.Logging;

namespace Kasi.Media;

public enum FfmpegErrorKind
{
    Unavailable,
    Timeout,
    ExitCode,
    InvalidInput,
}

/// <summary>The FFmpeg process boundary failed. Messages never contain a full local path.</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException(FfmpegErrorKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }

    public FfmpegErrorKind Kind { get; }
}

public sealed record FfmpegOptions(string FfmpegPath, string FfprobePath, TimeSpan? Timeout = null)
{
    public TimeSpan ProcessTimeout { get; init; } = Timeout ?? TimeSpan.FromMinutes(2);

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(FfmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(FfprobePath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ProcessTimeout, TimeSpan.Zero);
    }
}

public sealed record FfmpegResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record FfprobeTrackInfo(string CodecType, string CodecName);

public sealed record FfprobeResult(double? DurationSeconds, IReadOnlyList<FfprobeTrackInfo> Streams);

/// <summary>
/// Starts FFmpeg/ffprobe as an external process with an argument list (no shell), a local-file protocol whitelist, a Win32
/// job object with KILL_ON_JOB_CLOSE, bounded output and redacted stderr (TRD 13.4, KASI-MED-004).
/// </summary>
public sealed class FfmpegRunner(FfmpegOptions options)
{
    private static readonly Regex WindowsPath = new(@"[A-Za-z]:\\[^\r\n""']+", RegexOptions.Compiled);

    public FfmpegOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<FfmpegResult> RunAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyList<string>? sensitivePaths = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error" };
        args.AddRange(arguments);
        return await RunProcessAsync(Options.FfmpegPath, args, sensitivePaths, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FfmpegResult> RunFfprobeAsync(
        IReadOnlyList<string> arguments,
        IReadOnlyList<string>? sensitivePaths = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string> { "-v", "error" };
        args.AddRange(arguments);
        return await RunProcessAsync(Options.FfprobePath, args, sensitivePaths, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FfprobeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = await RunFfprobeAsync(
            ["-show_entries", "format=duration:stream=codec_type,codec_name", "-of", "json", path],
            [path],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new FfmpegException(FfmpegErrorKind.ExitCode, "ffprobe could not read the muxed file.");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var duration = document.RootElement.TryGetProperty("format", out var format)
            && format.TryGetProperty("duration", out var durationText)
            && double.TryParse(durationText.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (double?)null;
        var streams = new List<FfprobeTrackInfo>();
        if (document.RootElement.TryGetProperty("streams", out var streamArray))
        {
            foreach (var stream in streamArray.EnumerateArray())
            {
                streams.Add(new FfprobeTrackInfo(
                    stream.TryGetProperty("codec_type", out var type) ? type.GetString() ?? "" : "",
                    stream.TryGetProperty("codec_name", out var codec) ? codec.GetString() ?? "" : ""));
            }
        }

        return new FfprobeResult(duration, streams);
    }

    internal async Task<FfmpegResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyList<string>? sensitivePaths,
        CancellationToken cancellationToken)
    {
        Options.Validate();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new FfmpegException(FfmpegErrorKind.Unavailable, "FFmpeg could not be started.");
            }
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new FfmpegException(FfmpegErrorKind.Unavailable, "FFmpeg is not available at the configured path.", exception);
        }

        using var job = OperatingSystem.IsWindows() ? FfmpegJob.Assign(process) : null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.ProcessTimeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new FfmpegResult(
                process.ExitCode,
                Sanitize(await stdout.ConfigureAwait(false), sensitivePaths),
                Sanitize(await stderr.ConfigureAwait(false), sensitivePaths));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new FfmpegException(FfmpegErrorKind.Timeout, "FFmpeg did not finish before the process timeout.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    internal static string Sanitize(string value, IReadOnlyList<string>? sensitivePaths)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        foreach (var path in sensitivePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            value = value.Replace(path, Redact.Path(path), StringComparison.OrdinalIgnoreCase);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                value = value.Replace(directory, Redact.Path(directory), StringComparison.OrdinalIgnoreCase);
            }
        }

        return WindowsPath.Replace(value, match => Redact.Path(match.Value.Trim()));
    }
}
