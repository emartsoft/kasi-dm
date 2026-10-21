using System.Buffers.Binary;

namespace Kasi.NativeHost.Framing;

/// <summary>
/// Writes browser native messaging frames (4-byte little-endian length, then UTF-8 JSON) to stdout. One lock and one flush per frame, so two
/// pumps can never interleave bytes. A payload over the browser's 1 MiB limit is refused here as a last line of defence; the relay replaces
/// such a message with an error before it gets this far (TRD 9.3).
/// </summary>
internal sealed class NativeFrameWriter(Stream output) : IDisposable
{
    public const int MaxFrameBytes = 1024 * 1024;

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > MaxFrameBytes)
        {
            throw new InvalidOperationException("The payload length is outside the browser's limit.");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _lock.Dispose();
}
