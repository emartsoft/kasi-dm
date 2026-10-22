using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace Kasi.Ipc.Transport;

/// <summary>Receives the command-line arguments of a second launch (ADR 0023). The values may hold URLs: never log them.</summary>
public delegate ValueTask HandOffHandler(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// The first instance's end of the argument hand-off: a per-user pipe (<c>kasi-&lt;SID&gt;-open</c>) with the same access list as the browser
/// pipe (current user only, NETWORK denied). Each connection carries one JSON line, <c>{"args":["..."]}</c>; anything longer, slower or
/// malformed is dropped without a word.
/// </summary>
public sealed class HandOffServer : IAsyncDisposable
{
    public const int MaxLineBytes = 64 * 1024;
    public const int MaxArguments = 64;

    private readonly string _pipeName;
    private readonly HandOffHandler _handler;
    private readonly TimeSpan _readTimeout;
    private readonly TimeProvider _time;
    private readonly SecurityIdentifier _user = KasiPipeNames.CurrentUser();
    private readonly ConcurrentDictionary<Task, byte> _serving = new();
    private readonly CancellationTokenSource _stopping = new();
    private NamedPipeServerStream? _listening;
    private Task? _acceptLoop;

    public HandOffServer(string pipeName, HandOffHandler handler, TimeSpan? readTimeout = null, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        ArgumentNullException.ThrowIfNull(handler);
        _pipeName = pipeName;
        _handler = handler;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(5);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Creates the pipe (failing if the name is taken) and starts accepting.</summary>
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

    /// <summary>The access list of the pipe as created, for tests.</summary>
    internal PipeSecurity ReadSecurity() =>
        _listening is { } pipe ? pipe.GetAccessControl() : throw new InvalidOperationException("The server is not started.");

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listening?.Dispose();
        if (_acceptLoop is not null)
        {
#pragma warning disable VSTHRD003 // started by Start() on this server
            await _acceptLoop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

#pragma warning disable VSTHRD003 // the serve tasks were started by this server's accept loop
        await Task.WhenAll(_serving.Keys).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        _stopping.Dispose();
    }

    private NamedPipeServerStream CreateInstance(bool first) =>
        NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.In,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: MaxLineBytes,
            outBufferSize: 0,
            PipeSecurityFactory.Create(_user));

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

            // ponytail: one task per connection, unbounded; only this user can connect and each is cut off after the read timeout
            var serving = ServeAsync(accepted, cancellationToken);
            _serving[serving] = 0;
            _ = serving.ContinueWith(t => _serving.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe.ConfigureAwait(false))
        {
            try
            {
                using var timeout = new CancellationTokenSource(_readTimeout, _time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                var arguments = Parse(await ReadLineAsync(pipe, linked.Token).ConfigureAwait(false));
                if (arguments is not null)
                {
                    await _handler(arguments, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // the peer went away, was too slow, or the server is stopping
            }
        }
    }

    private static async Task<byte[]?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxLineBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var end = Array.IndexOf(buffer, (byte)'\n', length, read);
            if (end >= 0)
            {
                return buffer[..end];
            }

            length += read;
        }

        return length == 0 || length == buffer.Length ? null : buffer[..length];
    }

    private static List<string>? Parse(byte[]? line)
    {
        if (line is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("args", out var args)
                || args.ValueKind != JsonValueKind.Array
                || args.GetArrayLength() > MaxArguments)
            {
                return null;
            }

            var result = new List<string>(args.GetArrayLength());
            foreach (var item in args.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                result.Add(item.GetString()!);
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>The second instance's end of the hand-off.</summary>
public static class HandOffClient
{
    /// <summary>
    /// Sends the arguments to the first instance and returns once it has read them. Synchronous on purpose: <c>Main</c> is an STA thread that
    /// must not await. False when nobody serves the pipe within <paramref name="timeout"/>, the pipe is not the current user's, or the write fails.
    /// </summary>
    public static bool TrySend(string pipeName, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
            pipe.Connect((int)timeout.TotalMilliseconds);
            if (ServerIdentity.OwnerOfServer(pipe) != KasiPipeNames.CurrentUser())
            {
                return false;
            }

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var json = new Utf8JsonWriter(buffer))
            {
                json.WriteStartObject();
                json.WriteStartArray("args");
                foreach (var argument in arguments)
                {
                    json.WriteStringValue(argument);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            buffer.GetSpan(1)[0] = (byte)'\n';
            buffer.Advance(1);
            if (buffer.WrittenCount > HandOffServer.MaxLineBytes)
            {
                return false;
            }

            pipe.Write(buffer.WrittenSpan);
            pipe.Flush();
            pipe.WaitForPipeDrain();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
