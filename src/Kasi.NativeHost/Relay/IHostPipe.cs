using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.NativeHost.Relay;

/// <summary>The host's side of the pipe to Kasi, so the relay can run in a test without a browser or a real Kasi.</summary>
internal interface IHostPipe : IAsyncDisposable
{
    Task SendAsync(IpcMessage message, CancellationToken cancellationToken);

    /// <summary>The next message from Kasi, or null when the pipe closed. A malformed message throws <see cref="IpcProtocolException"/>.</summary>
    Task<IpcMessage?> ReceiveAsync(CancellationToken cancellationToken);
}

internal interface IHostPipeConnector
{
    /// <exception cref="AppUnavailableException">Kasi is not installed or did not open its pipe in time.</exception>
    Task<IHostPipe> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Kasi cannot be reached; <see cref="Code"/> is <c>APP_NOT_RUNNING</c> or <c>APP_NOT_INSTALLED</c>, and the extension then resumes the browser's own download.</summary>
internal sealed class AppUnavailableException(IpcErrorCode code) : Exception($"Kasi is not available ({code}).")
{
    public IpcErrorCode Code { get; } = code;
}

internal sealed class KasiPipeHostPipe(KasiPipeClient client) : IHostPipe
{
    public Task SendAsync(IpcMessage message, CancellationToken cancellationToken) => client.SendAsync(message, cancellationToken);

    public Task<IpcMessage?> ReceiveAsync(CancellationToken cancellationToken) => client.ReceiveAsync(cancellationToken);

    public ValueTask DisposeAsync() => client.DisposeAsync();
}
