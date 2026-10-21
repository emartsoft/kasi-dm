using System.Buffers.Binary;

namespace Kasi.Ipc.Transport;

/// <summary>A frame was malformed (length 0, over the cap, or cut short). The connection that sent it is closed.</summary>
public sealed class IpcFramingException(string message) : IOException(message);

/// <summary>
/// The wire frame, the same shape as the browser's native messaging frame: a 4-byte little-endian length, then that many bytes of UTF-8 JSON.
/// A length of 0 or over <see cref="MaxFrameBytes"/> is an error. The buffer grows with the bytes that actually arrive, so a peer that claims
/// 64 MiB and sends nothing costs one small buffer, not 64 MiB.
/// </summary>
public static class IpcFraming
{
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    private const int FirstChunk = 64 * 1024;

    /// <summary>Reads one frame, or returns null when the peer closed the stream cleanly before a new frame.</summary>
    public static async ValueTask<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var got = 0;
        while (got < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(got), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return got == 0 ? null : throw new IpcFramingException("The stream ended inside a frame header.");
            }

            got += read;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > MaxFrameBytes)
        {
            throw new IpcFramingException("The frame length is outside the allowed range.");
        }

        var buffer = new byte[Math.Min((int)length, FirstChunk)];
        var filled = 0;
        while (filled < length)
        {
            if (filled == buffer.Length)
            {
                Array.Resize(ref buffer, (int)Math.Min(length, (long)buffer.Length * 2));
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IpcFramingException("The stream ended inside a frame.");
            }

            filled += read;
        }

        return buffer;
    }

    public static async ValueTask WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > MaxFrameBytes)
        {
            throw new IpcFramingException("The payload length is outside the allowed range.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
