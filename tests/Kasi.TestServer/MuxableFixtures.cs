using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kasi.TestServer;

/// <summary>
/// The real, muxable fixtures under <c>tests/fixtures/media/muxable</c>: a 24 s 160×90 H.264/AAC clip split into six 4 s
/// TS segments and a six-part fMP4 DASH clip. The files are checked in; FFmpeg is never run to create them during a test.
/// </summary>
public static class MuxableFixtures
{
    public const int SegmentCount = 6;
    public const int SegmentSeconds = 4;

    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<string> Master = new(() => File.ReadAllText(Path.Combine(Root.Value, "hls", "master.m3u8")));
    private static readonly Lazy<string> Media = new(() => File.ReadAllText(Path.Combine(Root.Value, "hls", "index.m3u8")));
    private static readonly Lazy<string> DashManifest = new(() => File.ReadAllText(Path.Combine(Root.Value, "dash", "manifest.mpd")));
    private static readonly Lazy<byte[]> Key = new(() => SHA256.HashData("kasi-muxable-key"u8)[..16]);

    public static string HlsMaster() => Master.Value;

    public static string HlsMedia(bool encrypted)
    {
        var text = Media.Value;
        if (!encrypted)
        {
            return text;
        }

        var newline = text.IndexOf('\n');
        return newline < 0
            ? text
            : text[..(newline + 1)] + "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n" + text[(newline + 1)..];
    }

    public static byte[] HlsSegment(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SegmentCount);
        return File.ReadAllBytes(Path.Combine(Root.Value, "hls", $"segment-{index}.ts"));
    }

    public static byte[] HlsEncryptedSegment(int index)
    {
        using var aes = Aes.Create();
        aes.Key = Key.Value;
        var iv = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(12), index);
        return aes.EncryptCbc(HlsSegment(index), iv);
    }

    public static byte[] KeyBytes() => Key.Value;

    public static string DashManifestText() => DashManifest.Value;

    public static byte[] DashInit() => File.ReadAllBytes(Path.Combine(Root.Value, "dash", "init.mp4"));

    public static byte[] DashSegment(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, SegmentCount);
        return File.ReadAllBytes(Path.Combine(Root.Value, "dash", $"seg-{number}.m4s"));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kasi.slnx")))
            {
                return Path.Combine(directory.FullName, "tests", "fixtures", "media", "muxable");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root (Kasi.slnx) was not found from the test output.");
    }
}
