using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kasi.TestServer;

/// <summary>
/// Deterministic file content (TPL 3.1): byte <c>i</c> of file <c>seed</c> is
/// <c>SHA-256(UTF-8(seed) ‖ int64-LE(i / 4096))[i % 32]</c>. Tests recompute expected bytes and hashes
/// from the seed and size alone, so no large fixtures are stored.
/// </summary>
public static class GeneratedContent
{
    public const int BlockSize = 4096;
    public const int MaxSeedLength = 64;

    public static void Fill(string seed, long offset, Span<byte> destination)
    {
        var seedBytes = Encoding.UTF8.GetBytes(seed);
        var input = new byte[seedBytes.Length + sizeof(long)];
        seedBytes.CopyTo(input, 0);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];

        var written = 0;
        var position = offset;
        while (written < destination.Length)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(seedBytes.Length), position / BlockSize);
            SHA256.HashData(input, hash);

            var take = (int)Math.Min(BlockSize - (position % BlockSize), destination.Length - written);
            for (var i = 0; i < take; i++)
            {
                destination[written + i] = hash[(int)((position + i) % hash.Length)];
            }

            written += take;
            position += take;
        }
    }

    public static byte[] Bytes(string seed, long size)
    {
        var bytes = new byte[size];
        Fill(seed, 0, bytes);
        return bytes;
    }

    /// <summary>Lower-case hex SHA-256 of the first <paramref name="size"/> bytes of file <paramref name="seed"/>.</summary>
    public static string Sha256Hex(string seed, long size)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        for (long offset = 0; offset < size; offset += buffer.Length)
        {
            var chunk = buffer.AsSpan(0, (int)Math.Min(buffer.Length, size - offset));
            Fill(seed, offset, chunk);
            hasher.AppendData(chunk);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}
