using System.Text.Json;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Kasi.NativeHost.Framing;

namespace Kasi.NativeHost.Relay;

/// <summary>
/// The bridge: browser frames from stdin go to Kasi, Kasi's messages go to stdout, both at the same time (Kasi pushes <c>rules</c> and
/// <c>refreshLink</c> whenever it likes). Everything from the browser is parsed by <see cref="IpcSerializer"/> before it is forwarded.
/// Failure policy (docs/plans/m2-2.md section 1): a frame that cannot be trusted to be a frame ends the host with <see cref="ExitFraming"/>;
/// a well-framed invalid message gets an <c>error</c> reply and the host keeps running. Logs carry fixed text only, never message content.
/// </summary>
internal sealed class HostRelay(Stream stdin, NativeFrameWriter output, IHostPipeConnector connector, TextWriter log) : IDisposable
{
    public const int ExitFraming = 2;
    public const int ExitPipeLost = 3;

    private const string UnknownId = "unknown";

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IHostPipe? _pipe;
    private Hello? _hello;
    private Task? _pump;

    /// <summary>Runs until stdin closes (0), a frame is broken (<see cref="ExitFraming"/>) or Kasi is lost for good (<see cref="ExitPipeLost"/>).</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inbound = Task.Run(() => PumpBrowserAsync(stop.Token), CancellationToken.None);
        try
        {
#pragma warning disable VSTHRD003 // _exit is completed by our own pump, not foreign work
            return await (await Task.WhenAny(inbound, _exit.Task).ConfigureAwait(false)).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            if (_pipe is { } pipe)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _connectLock.Dispose();

    private Task LogAsync(string line) => log.WriteLineAsync(line);

    private async Task<int> PumpBrowserAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                byte[]? frame;
                try
                {
                    frame = await IpcFraming.ReadAsync(stdin, ct).ConfigureAwait(false);
                }
                catch (IpcFramingException)
                {
                    await LogAsync("host: broken frame from the browser, exiting").ConfigureAwait(false);
                    return ExitFraming;
                }

                if (frame is null)
                {
                    return 0;
                }

                IpcMessage message;
                try
                {
                    message = IpcSerializer.Deserialize(frame);
                    if (message is Rules or RefreshLink)
                    {
                        // Kasi-to-browser types: Kasi would refuse them and close the connection
                        throw new IpcProtocolException(IpcErrorCode.InvalidMessage);
                    }
                }
                catch (IpcProtocolException ex)
                {
                    await ReplyAsync(TryReadId(frame) ?? UnknownId, ex.Code, ex.Message, ct).ConfigureAwait(false);
                    continue;
                }

                await ForwardAsync(message, ct).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            return 0; // stdout or stdin went away: the browser did
        }
    }

    private async Task ForwardAsync(IpcMessage message, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            IHostPipe pipe;
            try
            {
                pipe = await GetPipeAsync(ct).ConfigureAwait(false);
            }
            catch (AppUnavailableException ex)
            {
                await ReplyAsync(message.Id, ex.Code, ex.Message, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await pipe.SendAsync(message, ct).ConfigureAwait(false);
                if (message is Hello hello)
                {
                    _hello = hello;
                }

                return;
            }
            catch (IOException) when (attempt == 0)
            {
                await DropAsync(pipe).ConfigureAwait(false); // the pipe died under us: reconnect once and send again
            }
            catch (IOException)
            {
                await ReplyAsync(message.Id, IpcErrorCode.AppNotRunning, "Kasi is not available.", ct).ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>The current pipe, connecting (and starting Kasi) when there is none. A new pipe gets the last <c>hello</c> again, because Kasi closes a connection that does not open with one.</summary>
    private async Task<IHostPipe> GetPipeAsync(CancellationToken ct)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_pipe is not null)
            {
                return _pipe;
            }

            var pipe = await connector.ConnectAsync(ct).ConfigureAwait(false);
            try
            {
                if (_hello is { } hello)
                {
                    await pipe.SendAsync(hello, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _pipe = pipe;
            _pump ??= Task.Run(() => PumpPipeAsync(ct), CancellationToken.None);
            return pipe;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task DropAsync(IHostPipe pipe)
    {
        await _connectLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_pipe, pipe))
            {
                _pipe = null;
            }
        }
        finally
        {
            _connectLock.Release();
        }

        await pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpPipeAsync(CancellationToken ct)
    {
        var closedWithoutTraffic = 0;
        try
        {
            while (true)
            {
                var pipe = _pipe;
                IpcMessage? message = null;
                if (pipe is not null)
                {
                    try
                    {
                        message = await pipe.ReceiveAsync(ct).ConfigureAwait(false);
                    }
                    catch (IpcProtocolException)
                    {
                        await LogAsync("host: Kasi sent an invalid message, dropped").ConfigureAwait(false);
                        continue;
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        // treated like a close
                    }
                }

                if (message is not null)
                {
                    closedWithoutTraffic = 0;
                    await SendToBrowserAsync(message, ct).ConfigureAwait(false);
                    continue;
                }

                // Kasi closed the pipe: one reconnect through the launcher, otherwise the host ends. A pipe that closes again before
                // delivering anything is not retried a second time.
                if (pipe is not null)
                {
                    await DropAsync(pipe).ConfigureAwait(false);
                }

                if (++closedWithoutTraffic > 1)
                {
                    await LogAsync("host: Kasi closed the pipe again at once, exiting").ConfigureAwait(false);
                    _exit.TrySetResult(ExitPipeLost);
                    return;
                }

                try
                {
                    await GetPipeAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AppUnavailableException or IOException)
                {
                    await LogAsync("host: Kasi is gone and did not come back, exiting").ConfigureAwait(false);
                    _exit.TrySetResult(ExitPipeLost);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the host is shutting down
        }
        catch (IOException)
        {
            _exit.TrySetResult(0); // stdout went away: the browser did
        }
        catch (Exception ex)
        {
            await LogAsync($"host: pipe pump failed ({ex.GetType().Name}), exiting").ConfigureAwait(false);
            _exit.TrySetResult(ExitPipeLost);
        }
    }

    private Task ReplyAsync(string id, IpcErrorCode code, string detail, CancellationToken ct) =>
        SendToBrowserAsync(new IpcError { Id = id, Code = code, Detail = detail }, ct);

    /// <summary>Writes one message for the browser; one that would exceed the browser's 1 MiB limit becomes an <c>INTERNAL</c> error instead of a broken frame.</summary>
    private async Task SendToBrowserAsync(IpcMessage message, CancellationToken ct)
    {
        var payload = IpcSerializer.Serialize(message);
        if (payload.Length > NativeFrameWriter.MaxFrameBytes)
        {
            payload = IpcSerializer.Serialize(new IpcError { Id = message.Id, Code = IpcErrorCode.Internal, Detail = "The message is too large for the browser." });
        }

        await output.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>The correlation id of a message that failed to parse, when it has a usable one at the top level, so the extension can match the error to its request.</summary>
    private static string? TryReadId(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8Json);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isId = reader.ValueTextEquals("id"u8);
                reader.Read();
                if (isId)
                {
                    return reader.TokenType == JsonTokenType.String && reader.GetString() is { Length: >= 1 and <= IpcLimits.IdLength } id ? id : null;
                }

                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // not even JSON: no id
        }

        return null;
    }
}
