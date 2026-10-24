namespace Kasi.Media;

public enum MediaMuxContainer
{
    Auto,
    Mp4,
    Matroska,
}

public enum MediaMuxErrorKind
{
    NoStreams,
    FfmpegFailed,
    ProbeFailed,
    DurationMismatch,
    Io,
}

/// <summary>The mux or duration check failed. The work folder is kept for diagnosis.</summary>
public sealed class MediaMuxException : Exception
{
    public MediaMuxException(MediaMuxErrorKind kind, string message, string? sanitizedStderr = null, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
        SanitizedStderr = sanitizedStderr;
    }

    public MediaMuxErrorKind Kind { get; }

    /// <summary>FFmpeg's stderr after path redaction; safe for the job log.</summary>
    public string? SanitizedStderr { get; }
}

public sealed record MediaMuxOptions
{
    public required string OutputPath { get; init; }

    public MediaMuxContainer Container { get; init; } = MediaMuxContainer.Auto;

    public TimeSpan DurationTolerance { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Deletes the joined/segment work folder after a successful probe; a mismatch keeps it.</summary>
    public bool DeleteWorkDirectory { get; init; } = true;
}

public sealed record MediaMuxResult(string OutputPath, string Container, double DurationSeconds, bool WorkDirectoryDeleted);

/// <summary>
/// Performs the M3.4 hand-off after <see cref="MediaJobRunner"/>: stream-copy the joined video/audio into MP4 or MKV
/// (or an audio-only container), then verify the duration with ffprobe within one second and clean the work folder.
/// </summary>
public sealed class MediaMuxer(FfmpegRunner ffmpeg)
{
    public async Task<MediaMuxResult> MuxAsync(
        MediaJobResult job,
        MediaMuxOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);
        if (!Path.IsPathFullyQualified(options.OutputPath))
        {
            throw new ArgumentException("The output path must be absolute.", nameof(options));
        }

        var video = job.Streams.FirstOrDefault(stream => stream.Role == "video");
        var audio = job.Streams.FirstOrDefault(stream => stream.Role == "audio");
        var variant = job.Variant
            ?? throw new MediaMuxException(MediaMuxErrorKind.NoStreams, "The capture has no selected variant.");
        if (audio is null && video is null)
        {
            throw new MediaMuxException(MediaMuxErrorKind.NoStreams, "The capture has no audio or video stream.");
        }

        var output = options.OutputPath;
        var container = ChooseContainer(variant, video, options.Container);
        var extension = container switch
        {
            "mkv" => ".mkv",
            "m4a" => ".m4a",
            "mka" => ".mka",
            _ => ".mp4",
        };
        output = Path.ChangeExtension(output, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + ".part" + extension;
        File.Delete(temporary);

        var arguments = new List<string> { "-protocol_whitelist", "file" };
        if (video is not null)
        {
            arguments.Add("-i");
            arguments.Add(video.Path);
        }

        if (audio is not null)
        {
            arguments.Add("-i");
            arguments.Add(audio.Path);
        }

        if (video is not null)
        {
            arguments.AddRange(["-map", "0:v:0?"]);
            if (audio is not null)
            {
                arguments.AddRange(["-map", "1:a:0?"]);
            }
            else
            {
                // An HLS TS rendition usually carries its audio in the same joined stream.
                arguments.AddRange(["-map", "0:a:0?"]);
            }
        }
        else
        {
            arguments.AddRange(["-map", "0:a:0", "-vn"]);
        }

        arguments.AddRange(["-c", "copy"]);
        if (container == "mp4")
        {
            arguments.AddRange(["-movflags", "+faststart"]);
        }

        arguments.Add(temporary);
        var sensitive = new[] { video?.Path, audio?.Path, output, temporary }.OfType<string>().ToArray();
        var result = await ffmpeg.RunAsync(arguments, sensitive, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            TryDelete(temporary);
            throw new MediaMuxException(
                MediaMuxErrorKind.FfmpegFailed,
                "FFmpeg could not mux the capture.",
                result.StandardError);
        }

        File.Move(temporary, output, overwrite: true);
        FfprobeResult probe;
        try
        {
            probe = await ffmpeg.ProbeAsync(output, cancellationToken).ConfigureAwait(false);
        }
        catch (FfmpegException exception)
        {
            throw new MediaMuxException(MediaMuxErrorKind.ProbeFailed, "ffprobe could not read the muxed file.", null, exception);
        }

        if (probe.DurationSeconds is not { } actual)
        {
            throw new MediaMuxException(MediaMuxErrorKind.ProbeFailed, "ffprobe did not report a duration.");
        }

        var expected = job.DurationSeconds;
        if (expected > 0 && Math.Abs(actual - expected) > options.DurationTolerance.TotalSeconds)
        {
            throw new MediaMuxException(
                MediaMuxErrorKind.DurationMismatch,
                "The muxed duration does not match the manifest.");
        }

        var deleted = false;
        if (options.DeleteWorkDirectory && job.WorkDirectory is { } workDirectory && Directory.Exists(workDirectory)
            && !IsWithin(output, workDirectory))
        {
            Directory.Delete(workDirectory, recursive: true);
            deleted = true;
        }

        return new MediaMuxResult(output, container, actual, deleted);
    }

    private static string ChooseContainer(MediaVariant variant, MediaJoinedTrack? video, MediaMuxContainer requested)
    {
        if (requested == MediaMuxContainer.Mp4)
        {
            return "mp4";
        }

        if (requested == MediaMuxContainer.Matroska)
        {
            return "mkv";
        }

        if (video is null)
        {
            var codec = variant.Audio?.Codecs ?? "";
            return codec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ? "m4a" : "mka";
        }

        return IsMp4Compatible(variant) ? "mp4" : "mkv";
    }

    private static readonly string[] Mp4VideoCodecs = ["avc", "h264", "hev", "hvc", "av01"];

    private static readonly string[] Mp4AudioCodecs = ["mp4a", "aac", "ac-3", "ec-3", "ac3", "eac3"];

    private static bool IsMp4Compatible(MediaVariant variant)
    {
        var videoCodec = variant.Video.Codecs ?? "";
        var audioCodec = variant.Audio?.Codecs ?? "";
        var videoOk = Mp4VideoCodecs
            .Any(codec => videoCodec.Contains(codec, StringComparison.OrdinalIgnoreCase));
        var audioOk = string.IsNullOrEmpty(audioCodec)
            || Mp4AudioCodecs
                .Any(codec => audioCodec.Contains(codec, StringComparison.OrdinalIgnoreCase));
        return videoOk && audioOk;
    }

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
