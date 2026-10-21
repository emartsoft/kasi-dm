using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Kasi.Ipc.Messages;

namespace Kasi.Ipc.Transport;

/// <summary>Answers one message from a registered host. Return the reply (an <c>ack</c>, an <c>error</c> or <c>rules</c>), or null for none.</summary>
public delegate ValueTask<IpcMessage?> IpcMessageHandler(IpcConnection connection, IpcMessage message, CancellationToken cancellationToken);

public sealed class KasiPipeServerOptions
{
    /// <summary>A connection that has not sent an acceptable <c>hello</c> by then is closed.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>More simultaneous connections than this are refused (one per open browser profile is normal).</summary>
    public int MaxConnections { get; init; } = 64;

    /// <summary>A host that does not take a broadcast within this time is dropped, so one stuck browser cannot hold up the others.</summary>
    public TimeSpan BroadcastTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The application's end of the browser protocol: a per-user named pipe with an explicit access list (current user only, NETWORK denied).
/// The first instance is created with <c>FirstPipeInstance</c>, so a process that already holds the name makes <see cref="Start"/> fail
/// loudly instead of Kasi serving a squatted pipe. Any number of hosts may connect; each must open with <c>hello</c>.
/// </summary>
public sealed class KasiPipeServer : IAsyncDisposable
{
    private const string ErrorId = "0";

    // a push is small; with a buffer the writer does not wait for the browser to read it
    private const int BufferSize = 64 * 1024;

    private readonly string _pipeName;
    private readonly IpcMessageHandler _handler;
    private readonly KasiPipeServerOptions _options;
    private readonly TimeProvider _time;
    private readonly SecurityIdentifier _user;
    private readonly ConcurrentDictionary<long, IpcConnection> _connections = new();
    private readonly ConcurrentDictionary<Task, byte> _serving = new();
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeServerStream? _listening;
    private Task? _acceptLoop;
    private long _nextNumber;

    public KasiPipeServer(string pipeName, IpcMessageHandler handler, KasiPipeServerOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(handler);
        _pipeName = pipeName;
        _handler = handler;
        _options = options ?? new KasiPipeServerOptions();
        _time = timeProvider ?? TimeProvider.System;
        _user = KasiPipeNames.CurrentUser();
    }

    /// <summary>The hosts that have said hello and are still connected.</summary>
    public IReadOnlyCollection<IpcConnection> Connections => [.. _connections.Values.Where(c => c.IsRegistered)];

    /// <summary>Creates the pipe (failing if the name is already taken) and starts accepting hosts.</summary>
    /// <exception cref="IOException">Another process already has a pipe of this name.</exception>
    public void Start()
    {
        if (_acceptLoop is not null)
        {
            throw new InvalidOperationException("The server was already started.");
        }

        _listening = CreateInstance(first: true);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stopping.Token));
    }

    /// <summary>Sends to one host.</summary>
    public Task SendAsync(IpcConnection connection, IpcMessage message, CancellationToken cancellationToken) =>
        connection.SendAsync(message, cancellationToken);

    /// <summary>
    /// Sends to every registered host at the same time; a host that cannot be written to, or does not take the message within
    /// <see cref="KasiPipeServerOptions.BroadcastTimeout"/>, is dropped and the others still get it. Returns how many received it.
    /// </summary>
    public async Task<int> BroadcastAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        var sends = Connections.Select(connection => TrySendAsync(connection, message, cancellationToken)).ToList();
        var results = await Task.WhenAll(sends).ConfigureAwait(false);
        return results.Count(delivered => delivered);
    }

    private async Task<bool> TrySendAsync(IpcConnection connection, IpcMessage message, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.BroadcastTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await connection.SendAsync(message, linked.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            connection.Dispose();
            return false;
        }
    }

    /// <summary>The access list of the pipe as created, for tests.</summary>
    internal PipeSecurity ReadSecurity() =>
        _listening is { } pipe ? pipe.GetAccessControl() : throw new InvalidOperationException("The server is not started.");

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listening?.Dispose();
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        if (_acceptLoop is not null)
        {
            try
            {
#pragma warning disable VSTHRD003 // started by Start() on this server
                await _acceptLoop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

#pragma warning disable VSTHRD003 // the serve tasks were started by this server's accept loop
        await Task.WhenAll(_serving.Keys).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        _stopping.Dispose();
    }

    private NamedPipeServerStream CreateInstance(bool first)
    {
        var options = PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: BufferSize,
            outBufferSize: BufferSize,
            PipeSecurityFactory.Create(_user));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var next = _listening!;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await next.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // a client that connected and left again; listen on a fresh instance
                await next.DisposeAsync().ConfigureAwait(false);
                next = CreateInstance(first: false);
                _listening = next;
                continue;
            }

            var accepted = next;
            next = CreateInstance(first: false);
            _listening = next;

            if (_connections.Count >= _options.MaxConnections)
            {
                await accepted.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var connection = new IpcConnection(accepted, Interlocked.Increment(ref _nextNumber), _time);
            _connections[connection.Number] = connection;
            var serving = ServeAsync(connection, cancellationToken);
            _serving[serving] = 0;
            _ = serving.ContinueWith(t => _serving.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(IpcConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await ReadLoopAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or IpcProtocolException)
        {
            // the peer went away, sent a broken frame, or the server is stopping: the connection ends, the server carries on
        }
        finally
        {
            _connections.TryRemove(connection.Number, out _);
            connection.Dispose();
        }
    }

    private async Task ReadLoopAsync(IpcConnection connection, CancellationToken cancellationToken)
    {
        // the first frame has to be a good hello, and it has to come soon
        using var helloTimeout = new CancellationTokenSource(_options.HelloTimeout, _time);
        using var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, helloTimeout.Token);

        while (true)
        {
            var frame = await IpcFraming.ReadAsync(connection.Stream, connection.IsRegistered ? cancellationToken : firstFrame.Token).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            IpcMessage message;
            try
            {
                message = IpcSerializer.Deserialize(frame);
            }
            catch (IpcProtocolException ex)
            {
                await ReplyErrorAsync(connection, ErrorId, ex.Code, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!connection.IsRegistered)
            {
                if (message is not Hello hello)
                {
                    await ReplyErrorAsync(connection, message.Id, IpcErrorCode.InvalidMessage, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (hello.ProtocolMin > IpcProtocol.Version || hello.ProtocolMax < IpcProtocol.Version)
                {
                    await ReplyErrorAsync(connection, hello.Id, IpcErrorCode.UnsupportedVersion, cancellationToken).ConfigureAwait(false);
                    return;
                }

                connection.ProtocolVersion = IpcProtocol.Version;
                connection.Client = hello.Client;
            }

            if (connection.TryComplete(message))
            {
                continue;
            }

            IpcMessage? reply;
            try
            {
                reply = await _handler(connection, message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // a handler bug must not take the connection down, and its message may hold a URL: say only that it failed
                reply = new IpcError { Id = message.Id, Code = IpcErrorCode.Internal };
            }

            if (reply is not null)
            {
                await connection.SendAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReplyErrorAsync(IpcConnection connection, string id, IpcErrorCode code, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(new IpcError { Id = id, Code = code }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or IpcProtocolException)
        {
            // the peer is already gone
        }
    }
}
