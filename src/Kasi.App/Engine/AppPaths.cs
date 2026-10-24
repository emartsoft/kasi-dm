using Kasi.Media;

namespace Kasi.App.Engine;

/// <summary>Where Kasi keeps its data and saves files. <c>KASI_DATA_DIR</c> moves the data folder (tests; a portable copy).</summary>
internal static class AppPaths
{
    public const string DataFolderVariable = "KASI_DATA_DIR";

    /// <summary>Tests set this so what the browser hands to Kasi is saved in a folder of their own, not in the user's Downloads.</summary>
    public const string DownloadsFolderVariable = "KASI_DOWNLOADS_DIR";

    public static string DataFolder(string? endpointSuffix = null)
    {
        var overridden = Environment.GetEnvironmentVariable(DataFolderVariable);
        var root = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kasi")
            : overridden;
        return string.IsNullOrEmpty(endpointSuffix) ? root : Path.Combine(root, endpointSuffix);
    }

    public static string DatabasePath(string? endpointSuffix = null) => Path.Combine(DataFolder(endpointSuffix), "kasi.db");

    /// <summary>The packaged FFmpeg pair, or a pair named by KASI_FFMPEG/KASI_FFPROBE for development and tests.</summary>
    public static FfmpegOptions? Ffmpeg()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("KASI_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("KASI_FFPROBE");
        if (!string.IsNullOrWhiteSpace(ffmpeg) && !string.IsNullOrWhiteSpace(ffprobe) && File.Exists(ffmpeg) && File.Exists(ffprobe))
        {
            return new FfmpegOptions(ffmpeg, ffprobe);
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        var bundledFfmpeg = Path.Combine(bundled, "ffmpeg.exe");
        var bundledFfprobe = Path.Combine(bundled, "ffprobe.exe");
        return File.Exists(bundledFfmpeg) && File.Exists(bundledFfprobe)
            ? new FfmpegOptions(bundledFfmpeg, bundledFfprobe)
            : null;
    }

    // ponytail: the profile's Downloads folder by convention; the Known Folder API (it can be moved) comes with the Settings General page.
    public static string DownloadsFolder() => DownloadsFolder(Environment.GetEnvironmentVariable(DownloadsFolderVariable));

    internal static string DownloadsFolder(string? overridden) =>
        string.IsNullOrWhiteSpace(overridden) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") : overridden;
}
