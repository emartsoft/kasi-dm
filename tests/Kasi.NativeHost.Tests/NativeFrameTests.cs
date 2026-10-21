#pragma warning disable CA2000 // the fakes own nothing that needs disposing
using System.Text;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Kasi.NativeHost.Framing;
using Kasi.NativeHost.Relay;

namespace Kasi.NativeHost.Tests;

[Trait("TC", "TC-BRW-002")]
[Trait("Requirement", "KASI-BRW-006")]
public sealed class NativeFrameTests
{
    [Fact]
    public async Task A_written_frame_is_a_little_endian_length_and_the_payload()
    {
        var stdout = new TestStdout();
        using var writer = new NativeFrameWriter(stdout);

        await writer.WriteAsync(Encoding.UTF8.GetBytes("{}"), CancellationToken.None);

        stdout.Bytes().Should().Equal(2, 0, 0, 0, (byte)'{', (byte)'}');
    }

    [Fact]
    public async Task The_browser_limit_of_one_MiB_is_allowed_and_one_byte_more_is_refused()
    {
        var stdout = new TestStdout();
        using var writer = new NativeFrameWriter(stdout);

        await writer.WriteAsync(new byte[NativeFrameWriter.MaxFrameBytes], CancellationToken.None);
        var tooBig = () => writer.WriteAsync(new byte[NativeFrameWriter.MaxFrameBytes + 1], CancellationToken.None);
        var empty = () => writer.WriteAsync([], CancellationToken.None);

        await tooBig.Should().ThrowAsync<InvalidOperationException>();
        await empty.Should().ThrowAsync<InvalidOperationException>();
        stdout.Length.Should().Be(4 + NativeFrameWriter.MaxFrameBytes);
    }

    [Fact]
    public async Task Frames_written_by_many_tasks_at_once_never_interleave()
    {
        var stdout = new TestStdout();
        using var writer = new NativeFrameWriter(stdout);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => writer.WriteAsync(IpcSerializer.Serialize(Msg.Ack("a" + i)), CancellationToken.None), TestContext.Current.CancellationToken)));

        stdout.Messages().Select(m => m.Id).Should().BeEquivalentTo(Enumerable.Range(0, 200).Select(i => "a" + i));
    }

    [Fact]
    public async Task A_frame_arriving_one_byte_at_a_time_is_read_whole()
    {
        var connector = new FakeConnector();
        var pipe = new FakePipe();
        await using var session = new RelaySession(connector.Then(pipe), stdinChunk: 1);

        session.Stdin.Feed(Msg.Hello());

        await WaitUntilAsync(() => pipe.SentCount == 1);
        pipe.Sent.Should().ContainSingle().Which.Should().BeOfType<Hello>();
    }

    [Theory]
    [InlineData("zero length")]
    [InlineData("over the 64 MiB cap")]
    [InlineData("stream ends inside the header")]
    [InlineData("stream ends inside the payload")]
    public async Task A_frame_that_cannot_be_trusted_ends_the_host_with_a_non_zero_code_and_writes_nothing(string case_)
    {
        await using var session = new RelaySession(new FakeConnector());

        switch (case_)
        {
            case "zero length": session.Stdin.Feed(TestStdin.Header(0)); break;
            case "over the 64 MiB cap": session.Stdin.Feed(TestStdin.Header(IpcFraming.MaxFrameBytes + 1u)); break;
            case "stream ends inside the header": session.Stdin.Feed([5, 0]); session.Stdin.Complete(); break;
            default: session.Stdin.Feed([.. TestStdin.Header(10), (byte)'{']); session.Stdin.Complete(); break;
        }

        (await session.ExitCodeAsync()).Should().Be(HostRelay.ExitFraming);
        session.Stdout.Length.Should().Be(0);
        session.Log.ToString().Should().Contain("broken frame");
    }

    [Fact]
    public async Task The_host_exits_cleanly_when_stdin_closes_between_frames()
    {
        await using var session = new RelaySession(new FakeConnector());

        session.Stdin.Complete();

        (await session.ExitCodeAsync()).Should().Be(0);
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the condition should hold within 10 s");
    }
}
