using System.Buffers;
using System.Security.Cryptography;

namespace Kasi.Engine;

/// <summary>Streams a sealed part file through the selected published checksum without buffering the file in memory.</summary>
public static class ChecksumVerifier
{
    public static async Task<string> ComputeAsync(string path, ChecksumAlgorithm algorithm, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var name = algorithm switch
        {
#pragma warning disable CA5351 // MD5 is used only to compare a user-supplied file checksum, never to protect data.
            ChecksumAlgorithm.Md5 => HashAlgorithmName.MD5,
#pragma warning restore CA5351
#pragma warning disable CA5350 // SHA-1 is used only to compare a user-supplied file checksum, never to protect data.
            ChecksumAlgorithm.Sha1 => HashAlgorithmName.SHA1,
#pragma warning restore CA5350
            ChecksumAlgorithm.Sha256 => HashAlgorithmName.SHA256,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

        using var hash = IncrementalHash.CreateHash(name);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            PartFileWriter.WriteBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var streamScope = stream.ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(PartFileWriter.WriteBufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, PartFileWriter.WriteBufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, read));
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
