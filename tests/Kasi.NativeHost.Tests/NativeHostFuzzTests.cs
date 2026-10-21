#pragma warning disable CA2000 // the fakes own nothing that needs disposing
using System.Buffers.Binary;
using Kasi.Fuzz;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Kasi.NativeHost.Framing;
using Kasi.NativeHost.Relay;

namespace Kasi.NativeHost.Tests;

/// <summary>
/// TC-BRW-006: the host fed with malformed messages never crashes and always gives a defined answer (docs/plans/m2-2.md section 3).
/// The generator is <see cref="MalformedFrames"/>; the nightly SharpFuzz target is <see cref="HostMessageFuzz"/>.
/// </summary>
[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
public sealed class NativeHostFuzzTests
{
    private const int Seed = 20261021;
    private const int Count = 100_000;

    [Fact]
    public async Task One_hundred_thousand_malformed_messages_in_one_session_are_each_answered_with_an_error_and_the_host_keeps_serving()
    {
        var sink = new FrameSink();
        var pipe = new FakePipe { Reply = message => Msg.Ack(message.Id) };
        var connector = new FakeConnector().Then(pipe);
        var stdin = new GeneratedStdin(Count, Seed, TestStdin.Frame(IpcSerializer.Serialize(Msg.Download("final"))), sink.Acked);
        var log = new StringWriter();
        var before = GC.GetTotalMemory(forceFullCollection: true);

        int exit;
        using (var writer = new NativeFrameWriter(sink))
        using (var relay = new HostRelay(stdin, writer, connector, log))
        {
            exit = await relay.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(3), TestContext.Current.CancellationToken);
        }

        var growth = GC.GetTotalMemory(forceFullCollection: true) - before;
        var because = $"seed {Seed}; replay frame k with MalformedFrames.Make({Seed}, k)";
        sink.FirstUnexpected.Should().BeNull(because);
        exit.Should().Be(0, because);
        sink.Refusals.Should().Be(Count, because);
        sink.Codes.Keys.Should().BeSubsetOf([IpcErrorCode.InvalidMessage, IpcErrorCode.UnsupportedVersion], because);
        sink.Codes.Should().ContainKey(IpcErrorCode.UnsupportedVersion, "the version kind must reach the version check");
        sink.Details.Should().HaveCountLessThanOrEqualTo(2, "the detail is fixed text, never the input");
        connector.Connects.Should().Be(1, "nothing malformed may reach Kasi: only the final valid message connected");
        pipe.Sent.Should().ContainSingle().Which.Id.Should().Be("final");
        sink.Others.Should().Be(1, "the one valid message gets its ack and nothing else is written");
        log.ToString().Should().BeEmpty("a refused message is not worth a log line, and the log never carries content");
        growth.Should().BeLessThan(16 * 1024 * 1024, "the run must not accumulate memory");
    }

    [Fact]
    public async Task Every_framing_level_break_ends_the_host_with_code_2_after_the_frames_before_it_and_never_writes_a_partial_frame()
    {
        for (var i = 0; i < 1_000; i++)
        {
            var rng = new Random(Seed + i);
            var before = rng.Next(0, 4);
            var stdin = new TestStdin();
            for (var f = 0; f < before; f++)
            {
                stdin.Feed(TestStdin.Frame(MalformedFrames.Make(Seed, (i * 4) + f)));
            }

            // an over-the-cap length must end the host at once: the stream stays open, so a host that waited for the payload would hang
            var (tail, endsStream) = BrokenTail(rng);
            stdin.Feed(tail);
            if (endsStream)
            {
                stdin.Complete();
            }

            var stdout = new TestStdout();
            var log = new StringWriter();
            using var writer = new NativeFrameWriter(stdout);
            using var relay = new HostRelay(stdin, writer, new FakeConnector(), log);

            var exit = await relay.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            var because = $"seed {Seed + i}";
            exit.Should().Be(HostRelay.ExitFraming, because);
            var replies = stdout.Messages(); // throws unless stdout is exactly whole frames
            replies.Should().HaveCount(before, because).And.AllBeOfType<IpcError>();
            log.ToString().Should().Be("host: broken frame from the browser, exiting" + Environment.NewLine, because);
        }
    }

    [Fact]
    public void Every_seed_and_saved_finding_still_passes_the_host_invariants()
    {
        foreach (var seed in HostMessageFuzz.Seeds())
        {
            HostMessageFuzz.Run([0, .. seed]); // as the payload of a frame
            HostMessageFuzz.Run([1, .. TestStdin.Frame(seed)]); // as raw stdin
        }

        // tests/fixtures/fuzz/host-crashes holds the minimised inputs of past findings; each must stay fixed
        var folder = Path.Combine(RepositoryRoot(), "tests", "fixtures", "fuzz", "host-crashes");
        foreach (var file in Directory.GetFiles(folder).Where(f => !f.EndsWith(".md", StringComparison.Ordinal)))
        {
            HostMessageFuzz.Run(File.ReadAllBytes(file));
        }

        for (var i = 0; i < 600; i++)
        {
            HostMessageFuzz.Run([0, .. MalformedFrames.Make(Seed, i)]);
        }
    }

    [Fact]
    public void An_empty_payload_is_a_defined_outcome_for_the_fuzz_target()
    {
        // a zero-length frame is a framing break: exit 2 and no output, which the target accepts
        HostMessageFuzz.Run([0]);
    }

    private static (byte[] Bytes, bool EndsStream) BrokenTail(Random rng)
    {
        switch (rng.Next(5))
        {
            case 0: // length zero
                return (TestStdin.Header(0), false);
            case 1: // over the 64 MiB cap, up to the largest length a header can carry
                return (TestStdin.Header((uint)rng.NextInt64(IpcFraming.MaxFrameBytes + 1L, uint.MaxValue + 1L)), false);
            case 2: // the stream ends inside the header
                return ([.. TestStdin.Header(10)[..rng.Next(1, 4)]], true);
            case 3: // the stream ends inside the payload
                {
                    var length = rng.Next(2, 300);
                    return ([.. TestStdin.Header((uint)length), .. new byte[rng.Next(0, length)]], true);
                }

            default: // over the cap, with plenty of bytes behind it that must never be read as frames
                return ([.. TestStdin.Header(uint.MaxValue), .. TestStdin.Frame(IpcSerializer.Serialize(Msg.Download("x")))], false);
        }
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kasi.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Kasi.slnx not found.");
    }
}

/// <summary>Frames made on demand, one per read, so 100,000 of them never sit in memory at once. After the last it waits for the test to be satisfied, then ends like a browser that went away.</summary>
internal sealed class GeneratedStdin(int count, int seed, byte[] finalFrame, Task release) : Stream
{
    private byte[] _current = [];
    private int _offset;
    private int _next;
    private bool _finalSent;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset == _current.Length)
        {
            if (_next < count)
            {
                _current = TestStdin.Frame(MalformedFrames.Make(seed, _next++));
            }
            else if (!_finalSent)
            {
                _current = finalFrame;
                _finalSent = true;
            }
            else
            {
#pragma warning disable VSTHRD003 // the release signal belongs to the test that owns this stream
                await release.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                return 0;
            }

            _offset = 0;
        }

        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
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

/// <summary>What the browser receives, checked as it arrives and not kept: <see cref="NativeFrameWriter"/> writes each frame in one call. The first reply that is not a refusal is remembered with its position.</summary>
internal sealed class FrameSink : Stream
{
    private readonly TaskCompletionSource _acked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _frames;

#pragma warning disable VSTHRD003 // a signal the sink completes itself, not foreign work
    public Task Acked => _acked.Task;
#pragma warning restore VSTHRD003

    public int Refusals { get; private set; }

    public int Others { get; private set; }

    public Dictionary<IpcErrorCode, int> Codes { get; } = [];

    public HashSet<string?> Details { get; } = [];

    public string? FirstUnexpected { get; private set; }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var position = _frames++;
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (buffer.Length != 4 + length)
        {
            FirstUnexpected ??= $"frame {position} is not one whole frame";
            return;
        }

        var message = IpcSerializer.Deserialize(buffer[4..]);
        if (message is IpcError { Code: IpcErrorCode.InvalidMessage or IpcErrorCode.UnsupportedVersion } error)
        {
            Refusals++;
            Codes[error.Code] = Codes.GetValueOrDefault(error.Code) + 1;
            Details.Add(error.Detail);
        }
        else if (message is Ack)
        {
            Others++;
            _acked.TrySetResult();
        }
        else
        {
            FirstUnexpected ??= $"frame {position} is a {message.GetType().Name}, not a refusal";
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
