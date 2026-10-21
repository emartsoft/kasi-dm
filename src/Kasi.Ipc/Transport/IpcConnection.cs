using System.Collections.Concurrent;
using Kasi.Ipc.Messages;

namespace Kasi.Ipc.Transport;

/// <summary>One connected native messaging host, as the server sees it. It is registered once its <c>hello</c> has been accepted.</summary>
public sealed class IpcConnection : IDisposable
{
    private readonly Stream _stream;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcMessage>> _pending = new(StringComparer.Ordinal);

    internal IpcConnection(Stream stream, long number, TimeProvider time)
    {
        _stream = stream;
        Number = number;
        _time = time;
    }

    internal Stream Stream => _stream;

    /// <summary>A number unique within the server, for logs and tests (never an identity of the peer).</summary>
    public long Number { get; }

    /// <summary>Null until the <c>hello</c> is accepted.</summary>
    public BrowserClient? Client { get; internal set; }

    public int ProtocolVersion { get; internal set; }

    public bool IsRegistered => Client is not null;

    public async Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        var payload = IpcSerializer.Serialize(message);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await IpcFraming.WriteAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Sends a message and waits for the <c>ack</c> or <c>error</c> that carries its id.</summary>
    /// <exception cref="TimeoutException">No answer within <paramref name="timeout"/>.</exception>
    public async Task<IpcMessage> RequestAsync(IpcMessage request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(request.Id, reply))
        {
            throw new InvalidOperationException("A request with this id is already waiting for its answer.");
        }

        using var timeoutSource = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            await SendAsync(request, linked.Token).ConfigureAwait(false);
            return await reply.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The host did not answer in time.");
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }
    }

    /// <summary>Hands an <c>ack</c> or <c>error</c> to the request that is waiting for it; false when none is.</summary>
    internal bool TryComplete(IpcMessage message) =>
        message is Ack or IpcError && _pending.TryRemove(message.Id, out var waiting) && waiting.TrySetResult(message);

    internal void FailPending()
    {
        foreach (var waiting in _pending.Values)
        {
            waiting.TrySetException(new IOException("The connection closed before the host answered."));
        }
    }

    public void Dispose()
    {
        FailPending();
        _stream.Dispose();
        _writeLock.Dispose();
    }
}
