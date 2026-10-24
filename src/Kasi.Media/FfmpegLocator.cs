using System.Runtime.InteropServices;

namespace Kasi.Media;

/// <summary>
/// Finds an FFmpeg/ffprobe pair for development and tests. The app supplies its packaged path explicitly; this helper is for
/// the environment variable <c>KASI_FFMPEG</c>/<c>KASI_FFPROBE</c> and a local <c>PATH</c> lookup.
/// </summary>
public static class FfmpegLocator
{
    public static bool TryFind(out FfmpegOptions options)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("KASI_FFMPEG");
        var ffprobe = Environment.GetEnvironmentVariable("KASI_FFPROBE");
        if (!string.IsNullOrWhiteSpace(ffmpeg) && !string.IsNullOrWhiteSpace(ffprobe)
            && File.Exists(ffmpeg) && File.Exists(ffprobe))
        {
            options = new FfmpegOptions(ffmpeg, ffprobe);
            return true;
        }

        var names = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Executable: "ffmpeg.exe", Probe: "ffprobe.exe")
            : (Executable: "ffmpeg", Probe: "ffprobe");
        var path = Environment.GetEnvironmentVariable("PATH");
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), names.Executable);
            var probe = Path.Combine(directory.Trim('"'), names.Probe);
            if (File.Exists(candidate) && File.Exists(probe))
            {
                options = new FfmpegOptions(candidate, probe);
                return true;
            }
        }

        options = null!;
        return false;
    }
}
