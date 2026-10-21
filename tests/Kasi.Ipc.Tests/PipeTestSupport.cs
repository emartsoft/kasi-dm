using System.IO.Pipes;
using System.Runtime.Versioning;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

[assembly: SupportedOSPlatform("windows")]

namespace Kasi.Ipc.Tests;

internal static class PipeTestSupport
{
    public static string NewPipeName() => "kasi-test-" + Guid.NewGuid().ToString("N");

    public static Hello Hello(string id = "h1", BrowserClient client = BrowserClient.Chrome) =>
        new() { Id = id, Client = client, ExtensionVersion = "1.0.0", ProtocolMin = 1, ProtocolMax = 1 };

    public static Rules Rules(string id) =>
        new() { Id = id, Enabled = true, Extensions = ["zip"], MimeTypes = [], ExcludedSites = [], BypassKey = BypassKey.Alt, AckTimeoutMs = 2000 };

    public static AddDownload Download(string id = "d1") => new()
    {
        Id = id,
        Url = "https://example.com/file.zip",
        FinalUrl = "https://example.com/file.zip",
        Filename = "file.zip",
        Mime = "application/zip",
        UserAgent = "test",
        Cookies = [],
    };

    /// <summary>A server whose handler answers <c>hello</c> with rules and everything else with an ack, and remembers what it saw.</summary>
    public static KasiPipeServer StartServer(string name, List<IpcMessage>? seen = null, KasiPipeServerOptions? options = null, TimeProvider? time = null)
    {
        var server = new KasiPipeServer(
            name,
            (_, message, _) =>
            {
                if (seen is not null)
                {
                    lock (seen)
                    {
                        seen.Add(message);
                    }
                }

                IpcMessage reply = message is Messages.Hello ? Rules(message.Id) : new Ack { Id = message.Id, Ok = true };
                return ValueTask.FromResult<IpcMessage?>(reply);
            },
            options,
            time);
        server.Start();
        return server;
    }

    public static async Task<NamedPipeClientStream> RawClientAsync(string name, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);
        return pipe;
    }

    public static async Task WriteRawAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>True when the server closed the stream (end of file), false if it was still open after the wait.</summary>
    public static async Task<bool> IsClosedByServerAsync(Stream stream, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(TimeSpan.FromSeconds(5));
        var buffer = new byte[1];
        try
        {
            while (await stream.ReadAsync(buffer, wait.Token) > 0)
            {
                // drain anything the server said before it closed
            }

            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Reads framed messages the server sent until it closes the stream.</summary>
    public static async Task<List<IpcMessage>> ReadAllAsync(Stream stream, CancellationToken ct)
    {
        var messages = new List<IpcMessage>();
        try
        {
            while (await IpcFraming.ReadAsync(stream, ct) is { } frame)
            {
                messages.Add(IpcSerializer.Deserialize(frame));
            }
        }
        catch (IOException)
        {
            // closed
        }

        return messages;
    }

    public static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The condition did not become true in 5 seconds.");
            }

            await Task.Delay(20, ct);
        }
    }
}
