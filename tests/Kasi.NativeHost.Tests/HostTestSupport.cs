using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.NativeHost.Framing;
using Kasi.NativeHost.Relay;

namespace Kasi.NativeHost.Tests;

/// <summary>What the browser writes: bytes fed by the test, handed out in chunks of the size the test picks (1 gives the one-byte partial reads).</summary>
internal sealed class TestStdin(int chunk = int.MaxValue) : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _offset;

    public void Feed(byte[] bytes) => _chunks.Writer.TryWrite(bytes);

    public void Feed(string json) => Feed(Frame(Encoding.UTF8.GetBytes(json)));

    public void Feed(IpcMessage message) => Feed(Frame(IpcSerializer.Serialize(message)));

    /// <summary>The browser went away.</summary>
    public void Complete() => _chunks.Writer.TryComplete();

    public static byte[] Frame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public static byte[] Header(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset == _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) || !_chunks.Reader.TryRead(out var next))
            {
                return 0;
            }

            _current = next;
            _offset = 0;
        }

        var count = Math.Min(Math.Min(buffer.Length, _current.Length - _offset), chunk);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>What the browser receives: every byte the host wrote, parsed back into frames.</summary>
internal sealed class TestStdout : Stream
{
    private readonly object _gate = new();
    private readonly List<byte> _bytes = [];

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            _bytes.AddRange(buffer.AsSpan(offset, count).ToArray());
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public byte[] Bytes()
    {
        lock (_gate)
        {
            return [.. _bytes];
        }
    }

    /// <summary>The frames written so far. Throws when the bytes are not exactly a run of whole frames: nothing but frames may reach stdout.</summary>
    public List<IpcMessage> Messages()
    {
        byte[] all;
        lock (_gate)
        {
            all = [.. _bytes];
        }

        var result = new List<IpcMessage>();
        var at = 0;
        while (at < all.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(at, 4));
            result.Add(IpcSerializer.Deserialize(all.AsSpan(at + 4, length)));
            at += 4 + length;
        }

        return result;
    }

    public async Task<List<IpcMessage>> WaitForAsync(int count)
    {
        for (var i = 0; i < 1000; i++)
        {
            var messages = Messages();
            if (messages.Count >= count)
            {
                return messages;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Expected {count} frames, saw {Messages().Count}.");
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length
    {
        get
        {
            lock (_gate)
            {
                return _bytes.Count;
            }
        }
    }

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Kasi's end of the pipe: records what the host sent and lets the test push messages or close the pipe.</summary>
internal sealed class FakePipe : IHostPipe
{
    private readonly Channel<IpcMessage?> _inbound = Channel.CreateUnbounded<IpcMessage?>();

    public List<IpcMessage> Sent { get; } = [];

    /// <summary>Answers each message the host sends; null means Kasi stays silent.</summary>
    public Func<IpcMessage, IpcMessage?>? Reply { get; set; }

    /// <summary>When set, the next <see cref="SendAsync"/> fails like a pipe that died.</summary>
    public bool Broken { get; set; }

    public Task SendAsync(IpcMessage message, CancellationToken cancellationToken)
    {
        if (Broken)
        {
            throw new IOException("The pipe is broken.");
        }

        lock (Sent)
        {
            Sent.Add(message);
        }

        if (Reply?.Invoke(message) is { } reply)
        {
            Push(reply);
        }

        return Task.CompletedTask;
    }

    public async Task<IpcMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (!await _inbound.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return _inbound.Reader.TryRead(out var message) ? message : null;
    }

    public void Push(IpcMessage message) => _inbound.Writer.TryWrite(message);

    /// <summary>Kasi closed its end.</summary>
    public void Close() => _inbound.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public int SentCount
    {
        get
        {
            lock (Sent)
            {
                return Sent.Count;
            }
        }
    }
}

/// <summary>Hands out the pipes (or failures) the test queued, one per connect.</summary>
internal sealed class FakeConnector : IHostPipeConnector
{
    private readonly Queue<Func<IHostPipe>> _next = new();

    public int Connects { get; private set; }

    public FakeConnector Then(IHostPipe pipe)
    {
        _next.Enqueue(() => pipe);
        return this;
    }

    public FakeConnector ThenFail(IpcErrorCode code)
    {
        _next.Enqueue(() => throw new AppUnavailableException(code));
        return this;
    }

    public Task<IHostPipe> ConnectAsync(CancellationToken cancellationToken)
    {
        Connects++;
        return Task.FromResult(_next.Count > 0 ? _next.Dequeue()() : throw new AppUnavailableException(IpcErrorCode.AppNotRunning));
    }
}

internal sealed class RelaySession : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public RelaySession(FakeConnector connector, int stdinChunk = int.MaxValue)
    {
        Stdin = new TestStdin(stdinChunk);
        Stdout = new TestStdout();
        Log = new StringWriter();
        Writer = new NativeFrameWriter(Stdout);
        Relay = new HostRelay(Stdin, Writer, connector, Log);
        Exit = Task.Run(() => Relay.RunAsync(_cts.Token));
    }

    public TestStdin Stdin { get; }

    public TestStdout Stdout { get; }

    public StringWriter Log { get; }

    public NativeFrameWriter Writer { get; }

    public HostRelay Relay { get; }

    public Task<int> Exit { get; }

    public async Task<int> ExitCodeAsync() => await Exit.WaitAsync(TimeSpan.FromSeconds(10));

    public async ValueTask DisposeAsync()
    {
        Stdin.Complete();
        await _cts.CancelAsync();
        try
        {
            await Exit.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // stopped on purpose
        }

        Relay.Dispose();
        Writer.Dispose();
        _cts.Dispose();
    }
}

internal static class Msg
{
    public static Hello Hello(string id = "h1") =>
        new() { Id = id, Client = BrowserClient.Chrome, ExtensionVersion = "1.0.0", ProtocolMin = 1, ProtocolMax = 1 };

    public static Rules Rules(string id) =>
        new() { Id = id, Enabled = true, Extensions = ["zip"], MimeTypes = [], ExcludedSites = [], BypassKey = BypassKey.Alt, AckTimeoutMs = 2000 };

    public static AddDownload Download(string id) => new()
    {
        Id = id,
        Url = "https://example.com/file.zip",
        FinalUrl = "https://example.com/file.zip",
        Filename = "file.zip",
        Mime = "application/zip",
        UserAgent = "test",
        Cookies = [],
    };

    public static Ack Ack(string id) => new() { Id = id, Ok = true };
}
