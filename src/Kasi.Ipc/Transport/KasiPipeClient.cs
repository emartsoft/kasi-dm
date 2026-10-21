using System.IO.Pipes;
using System.Security.Principal;
using Kasi.Ipc.Messages;

namespace Kasi.Ipc.Transport;

/// <summary>The pipe is served by a process that does not belong to the current user, so nothing was sent to it.</summary>
public sealed class IpcServerNotTrustedException() : IOException("The pipe is not served by a process of the current user.");

/// <summary>
/// The host's end of the browser protocol. Before it sends a byte it checks that the process serving the pipe belongs to the same Windows
/// user, so a process that squatted on the name cannot receive cookies or URLs.
/// </summary>
public sealed class KasiPipeClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private KasiPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>Raised once, when the server closes the pipe (seen by <see cref="ReceiveAsync"/>).</summary>
    public event Action? Closed;

    /// <summary>Connects to the current user's pipe.</summary>
    public static Task<KasiPipeClient> ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        ConnectAsync(KasiPipeNames.ForCurrentUser(), timeout, cancellationToken);

    /// <exception cref="TimeoutException">No server within <paramref name="timeout"/> (the host then starts Kasi and retries).</exception>
    /// <exception cref="IpcServerNotTrustedException">The server's owner is not the current user, or cannot be read.</exception>
    public static Task<KasiPipeClient> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken) =>
        ConnectAsync(pipeName, timeout, ServerIdentity.OwnerOfServer, cancellationToken);

    internal static async Task<KasiPipeClient> ConnectAsync(
        string pipeName,
        TimeSpan timeout,
        Func<NamedPipeClientStream, SecurityIdentifier?> ownerOfServer,
        CancellationToken cancellationToken)
    {
        // Identification: the server may learn who connected but cannot act as the client
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
            if (ownerOfServer(pipe) != KasiPipeNames.CurrentUser())
            {
                throw new IpcServerNotTrustedException();
            }

            return new KasiPipeClient(pipe);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        var payload = IpcSerializer.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IpcFraming.WriteAsync(_pipe, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>The next message from Kasi, or null when the pipe was closed. A malformed message throws <see cref="IpcProtocolException"/>.</summary>
    public async Task<IpcMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[]? frame;
        try
        {
            frame = await IpcFraming.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            frame = null;
        }

        if (frame is null)
        {
            Closed?.Invoke();
            return null;
        }

        return IpcSerializer.Deserialize(frame);
    }

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
