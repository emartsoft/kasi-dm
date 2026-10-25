using Kasi.Engine;

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
    FlaggedByAntivirus,
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

    /// <summary>The optional antivirus scan (off unless the setting is on); null scans nothing.</summary>
    public Kasi.Core.IFileScanner? Scanner { get; init; }

    /// <summary>Gets the safety step's fixed log lines.</summary>
    public Action<string>? Log { get; init; }
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

        var container = await ChooseContainerAsync(variant, video, audio, options.Container, cancellationToken).ConfigureAwait(false);
        var extension = container switch
        {
            "mkv" => ".mkv",
            "m4a" => ".m4a",
            "mka" => ".mka",
            _ => ".mp4",
        };
        var output = UniquePath(WithExtension(options.OutputPath, extension));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + ".part" + extension;
        File.Delete(temporary);

        var arguments = BuildArguments(video, audio, container, temporary);
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

        // The file is verified under its temporary name; only a good file ever appears at the real output path.
        FfprobeResult probe;
        try
        {
            probe = await ffmpeg.ProbeAsync(temporary, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (FfmpegException exception)
        {
            TryDelete(temporary);
            throw new MediaMuxException(MediaMuxErrorKind.ProbeFailed, "ffprobe could not read the muxed file.", null, exception);
        }

        if (probe.DurationSeconds is not { } actual)
        {
            TryDelete(temporary);
            throw new MediaMuxException(MediaMuxErrorKind.ProbeFailed, "ffprobe did not report a duration.");
        }

        var expected = job.DurationSeconds;
        if (expected > 0 && Math.Abs(actual - expected) > options.DurationTolerance.TotalSeconds)
        {
            TryDelete(temporary);
            throw new MediaMuxException(
                MediaMuxErrorKind.DurationMismatch,
                "The muxed duration does not match the manifest.");
        }

        // the scan and the Mark-of-the-Web, as for every download; a flagged file stays under its temporary name, never deleted by Kasi
        if (await FileSafety.ApplyAsync(temporary, null, options.Scanner, options.Log, cancellationToken).ConfigureAwait(false))
        {
            throw new MediaMuxException(MediaMuxErrorKind.FlaggedByAntivirus, FileSafety.FlaggedMessage);
        }

        try
        {
            File.Move(temporary, output, overwrite: false);
        }
        catch (IOException exception)
        {
            TryDelete(temporary);
            throw new MediaMuxException(MediaMuxErrorKind.Io, "The muxed file could not be moved into place.", null, exception);
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

    /// <summary>Every input gets its own protocol whitelist and, when known, a forced demuxer (never content sniffing).</summary>
    internal static List<string> BuildArguments(MediaJoinedTrack? video, MediaJoinedTrack? audio, string container, string output)
    {
        var arguments = new List<string>();
        AddInput(arguments, video);
        AddInput(arguments, audio);
        if (video is not null)
        {
            arguments.AddRange(["-map", "0:v:0?"]);
            // An HLS TS rendition usually carries its audio in the same joined stream.
            arguments.AddRange(audio is not null ? ["-map", "1:a:0?"] : ["-map", "0:a:0?"]);
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

        arguments.Add(output);
        return arguments;
    }

    private static void AddInput(List<string> arguments, MediaJoinedTrack? track)
    {
        if (track is null)
        {
            return;
        }

        arguments.AddRange(["-protocol_whitelist", "file"]);
        if (track.InputFormat is { } format)
        {
            arguments.AddRange(["-f", format]);
        }

        arguments.AddRange(["-i", track.Path]);
    }

    private async Task<string> ChooseContainerAsync(
        MediaVariant variant,
        MediaJoinedTrack? video,
        MediaJoinedTrack? audio,
        MediaMuxContainer requested,
        CancellationToken cancellationToken)
    {
        if (requested == MediaMuxContainer.Mp4)
        {
            return "mp4";
        }

        if (requested == MediaMuxContainer.Matroska)
        {
            return "mkv";
        }

        var videoCodec = variant.Video.Codecs ?? "";
        var audioCodec = variant.Audio?.Codecs ?? "";
        if (video is null)
        {
            if (audioCodec.Length == 0 && audio is not null)
            {
                audioCodec = (await DescribeAsync(audio, "audio", cancellationToken).ConfigureAwait(false)).Audio;
            }

            return audioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
                || audioCodec.Contains("aac", StringComparison.OrdinalIgnoreCase) ? "m4a" : "mka";
        }

        // The manifest had no CODECS: read the codec names from the joined file itself.
        if (videoCodec.Length == 0)
        {
            var described = await DescribeAsync(video, "video", cancellationToken).ConfigureAwait(false);
            videoCodec = described.Video;
            if (audio is null)
            {
                audioCodec = described.Audio;
            }
        }

        if (audio is not null && audioCodec.Length == 0)
        {
            audioCodec = (await DescribeAsync(audio, "audio", cancellationToken).ConfigureAwait(false)).Audio;
        }

        return IsMp4Compatible(videoCodec, audioCodec) ? "mp4" : "mkv";
    }

    private async Task<(string Video, string Audio)> DescribeAsync(MediaJoinedTrack track, string _, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await ffmpeg.ProbeAsync(track.Path, track.InputFormat, cancellationToken).ConfigureAwait(false);
            return (
                string.Join(',', probe.Streams.Where(stream => stream.CodecType == "video").Select(stream => stream.CodecName)),
                string.Join(',', probe.Streams.Where(stream => stream.CodecType == "audio").Select(stream => stream.CodecName)));
        }
        catch (FfmpegException)
        {
            return ("", ""); // unknown codecs fall back to MKV, which holds anything
        }
    }

    private static readonly string[] _mp4VideoCodecs = ["avc", "h264", "hev", "hvc", "av01", "av1"];

    private static readonly string[] _mp4AudioCodecs = ["mp4a", "aac", "ac-3", "ec-3", "ac3", "eac3", "mp3"];

    /// <summary>True when MP4 can hold these codec strings (or names) with a stream copy; empty or unknown codecs do not fit.</summary>
    public static bool IsMp4Compatible(string videoCodec, string audioCodec)
    {
        var videoOk = _mp4VideoCodecs
            .Any(codec => videoCodec.Contains(codec, StringComparison.OrdinalIgnoreCase));
        var audioOk = string.IsNullOrEmpty(audioCodec)
            || _mp4AudioCodecs
                .Any(codec => audioCodec.Contains(codec, StringComparison.OrdinalIgnoreCase));
        return videoOk && audioOk;
    }

    private static readonly string[] _containerExtensions = [".mp4", ".mkv", ".m4a", ".mka", ".m4v", ".webm"];

    /// <summary>Swaps a container extension but keeps dots that are part of the title (My.Show.S01E02 becomes My.Show.S01E02.mp4).</summary>
    internal static string WithExtension(string path, string extension) =>
        _containerExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
            ? Path.ChangeExtension(path, extension)
            : path + extension;

    /// <summary>Never replaces a file that is already there: name (1).mp4, name (2).mp4, ...</summary>
    internal static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var number = 1; number < 1000; number++)
        {
            var candidate = Path.Combine(directory, $"{name} ({number}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new MediaMuxException(MediaMuxErrorKind.Io, "No free output file name was found.");
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
