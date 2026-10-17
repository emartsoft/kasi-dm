using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Kasi.TestServer;

/// <summary>
/// What <c>/hls</c> and <c>/dash</c> serve, so tests can recompute expected segments. Segments are deterministic opaque
/// bytes (not decodable media): they exercise manifest parsing, quality selection, DRM refusal, parallel fetch, AES-128
/// decryption and ordered assembly. Muxable media for FFmpeg tests arrives with the media milestone.
/// </summary>
public static class StreamFixtures
{
    public const int SegmentCount = 6;
    public const int SegmentSeconds = 4;

    /// <summary>
    /// clear; aes128 (plain key URI, allowed); sample-aes, sample-aes-ctr and the three keyformat-* kinds carry DRM signals (TRD 7.3: refuse).
    /// </summary>
    public static IReadOnlyList<string> HlsKinds { get; } =
        ["clear", "aes128", "sample-aes", "sample-aes-ctr", "keyformat-fairplay", "keyformat-widevine", "keyformat-playready"];

    /// <summary>clear (SegmentTemplate) and segment-list are allowed; widevine, playready, fairplay and clearkey carry ContentProtection (refuse).</summary>
    public static IReadOnlyList<string> DashKinds { get; } = ["clear", "segment-list", "widevine", "playready", "fairplay", "clearkey"];

    /// <summary>HLS renditions and their segment size in bytes.</summary>
    public static IReadOnlyDictionary<string, int> HlsRenditions { get; } = new Dictionary<string, int>
    {
        ["360p"] = 32 * 1024,
        ["720p"] = 64 * 1024,
        ["audio"] = 16 * 1024,
    };

    public static IReadOnlyList<string> DashRepresentations { get; } = ["video-360p", "video-720p", "audio-en"];

    /// <summary>AES-128 key served at <c>/hls/aes128/key.bin</c>.</summary>
    public static byte[] HlsKey { get; } = SHA256.HashData("kasi-hls-key"u8)[..16];

    /// <summary>Plaintext of HLS segment <paramref name="index"/> (0-based), identical for every kind.</summary>
    public static byte[] HlsSegment(string rendition, int index) => GeneratedContent.Bytes($"hls/{rendition}/{index}", HlsRenditions[rendition]);

    /// <summary>What <c>/hls/aes128</c> serves: AES-128-CBC with PKCS#7 padding, IV = the segment's media sequence number as 16 big-endian bytes.</summary>
    public static byte[] HlsEncryptedSegment(string rendition, int index)
    {
        using var aes = Aes.Create();
        aes.Key = HlsKey;
        var iv = new byte[16];
        BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(12), index);
        return aes.EncryptCbc(HlsSegment(rendition, index), iv);
    }

    public static byte[] DashInit(string representation) => GeneratedContent.Bytes($"dash/{representation}/init", 1024);

    /// <summary>DASH segment <paramref name="number"/> (1-based, as in <c>$Number$</c>).</summary>
    public static byte[] DashSegment(string representation, int number) =>
        GeneratedContent.Bytes($"dash/{representation}/{number}", representation.StartsWith("audio", StringComparison.Ordinal) ? 16 * 1024 : 32 * 1024);
}
