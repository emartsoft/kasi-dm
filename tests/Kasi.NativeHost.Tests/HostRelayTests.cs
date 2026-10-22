#pragma warning disable CA2000 // the fakes own nothing that needs disposing
using Kasi.Ipc.Messages;
using Kasi.NativeHost.Relay;

namespace Kasi.NativeHost.Tests;

[Trait("TC", "TC-BRW-002")]
[Trait("Requirement", "KASI-BRW-006")]
public sealed class HostRelayTests
{
    private const string Secret = "https://secret.example/file?token=SEKRET-VALUE";

    private static FakePipe Server() => new() { Reply = m => m is Hello ? Msg.Rules(m.Id) : Msg.Ack(m.Id) };

    [Fact]
    public async Task A_valid_message_reaches_Kasi_and_its_answer_reaches_the_browser()
    {
        var pipe = Server();
        await using var session = new RelaySession(new FakeConnector().Then(pipe));

        session.Stdin.Feed(Msg.Hello("h1"));
        session.Stdin.Feed(Msg.Download("d1"));

        var seen = await session.Stdout.WaitForAsync(2);
        pipe.Sent.Select(m => m.Id).Should().Equal("h1", "d1");
        seen.Select(m => (m.GetType(), m.Id)).Should().Equal((typeof(Rules), "h1"), (typeof(Ack), "d1"));
    }

    [Fact]
    public async Task A_pushed_rules_message_arrives_while_a_request_is_still_in_flight()
    {
        var pipe = new FakePipe(); // Kasi never answers the download
        await using var session = new RelaySession(new FakeConnector().Then(pipe));
        session.Stdin.Feed(Msg.Download("d1"));
        await NativeFrameTests.WaitUntilAsync(() => pipe.SentCount == 1);

        pipe.Push(Msg.Rules("push1"));

        (await session.Stdout.WaitForAsync(1)).Should().ContainSingle().Which.Should().BeOfType<Rules>().Which.Id.Should().Be("push1");
    }

    [Fact]
    public async Task Both_directions_at_once_keep_every_frame_whole()
    {
        var pipe = Server();
        await using var session = new RelaySession(new FakeConnector().Then(pipe));

        var toKasi = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                session.Stdin.Feed(Msg.Download("d" + i));
            }
        }, TestContext.Current.CancellationToken);
        var toBrowser = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                pipe.Push(Msg.Rules("p" + i));
            }
        }, TestContext.Current.CancellationToken);
        await Task.WhenAll(toKasi, toBrowser);

        var seen = await session.Stdout.WaitForAsync(200);
        seen.Where(m => m is Ack).Select(m => m.Id).Should().BeEquivalentTo(Enumerable.Range(0, 100).Select(i => "d" + i));
        seen.Where(m => m is Rules).Select(m => m.Id).Should().BeEquivalentTo(Enumerable.Range(0, 100).Select(i => "p" + i));
    }

    [Theory]
    [InlineData("{ not json", "INVALID_MESSAGE")]
    [InlineData("{\"v\":1,\"id\":\"x1\",\"type\":\"nope\"}", "INVALID_MESSAGE")]
    [InlineData("{\"v\":1,\"id\":\"x1\",\"type\":\"openSettings\",\"extra\":1}", "INVALID_MESSAGE")]
    [InlineData("{\"v\":2,\"id\":\"x1\",\"type\":\"openSettings\"}", "UNSUPPORTED_VERSION")]
    public async Task A_well_framed_invalid_message_gets_an_error_and_the_host_keeps_running(string json, string code)
    {
        var pipe = Server();
        await using var session = new RelaySession(new FakeConnector().Then(pipe));

        session.Stdin.Feed(json);
        session.Stdin.Feed(Msg.Download("after"));

        var seen = await session.Stdout.WaitForAsync(2);
        var error = seen[0].Should().BeOfType<IpcError>().Subject;
        error.Code.Should().Be(code == "INVALID_MESSAGE" ? IpcErrorCode.InvalidMessage : IpcErrorCode.UnsupportedVersion);
        pipe.Sent.Select(m => m.Id).Should().Equal("after");
        session.Exit.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task The_error_for_an_invalid_message_carries_its_id_and_never_its_content()
    {
        await using var session = new RelaySession(new FakeConnector().Then(Server()));

        session.Stdin.Feed($"{{\"v\":1,\"id\":\"req-7\",\"type\":\"openSettings\",\"url\":\"{Secret}\"}}");

        var error = (IpcError)(await session.Stdout.WaitForAsync(1))[0];
        error.Id.Should().Be("req-7");
        $"{error.Detail}{session.Log}".Should().NotContain("SEKRET").And.NotContain("secret.example");
    }

    [Fact]
    public async Task An_invalid_message_without_a_usable_id_is_answered_with_a_placeholder_id()
    {
        await using var session = new RelaySession(new FakeConnector());

        session.Stdin.Feed("[1,2,3]");

        (await session.Stdout.WaitForAsync(1))[0].Id.Should().Be("unknown");
    }

    [Fact]
    public async Task A_message_that_only_Kasi_may_send_is_refused_before_it_reaches_the_pipe()
    {
        var pipe = Server();
        await using var session = new RelaySession(new FakeConnector().Then(pipe));

        session.Stdin.Feed(Msg.Rules("evil"));
        session.Stdin.Feed(Msg.Download("after"));

        var seen = await session.Stdout.WaitForAsync(2);
        seen[0].Should().BeOfType<IpcError>().Which.Id.Should().Be("evil");
        pipe.Sent.Select(m => m.Id).Should().Equal("after");
    }

    [Fact]
    public async Task A_message_from_Kasi_over_one_MiB_becomes_an_INTERNAL_error_not_a_broken_frame()
    {
        var pipe = new FakePipe();
        await using var session = new RelaySession(new FakeConnector().Then(pipe));
        session.Stdin.Feed(Msg.Download("d1"));
        await NativeFrameTests.WaitUntilAsync(() => pipe.SentCount == 1);

        // 1,000 entries of 256 characters that JSON escapes to six bytes each: over 1 MiB on the wire, inside every field cap
        var wide = string.Concat(Enumerable.Repeat("é", 256));
        pipe.Push(Msg.Rules("big") with { Extensions = [.. Enumerable.Repeat(wide, 1_000)] });
        pipe.Push(Msg.Rules("small"));

        var seen = await session.Stdout.WaitForAsync(2);
        seen[0].Should().BeOfType<IpcError>().Which.Should().Match<IpcError>(e => e.Id == "big" && e.Code == IpcErrorCode.Internal);
        seen[1].Id.Should().Be("small");
    }

    [Theory]
    [InlineData(IpcErrorCode.AppNotRunning)]
    [InlineData(IpcErrorCode.AppNotInstalled)]
    public async Task When_Kasi_cannot_be_reached_the_request_gets_that_error_and_the_host_keeps_running(IpcErrorCode code)
    {
        var pipe = Server();
        await using var session = new RelaySession(new FakeConnector().ThenFail(code).Then(pipe));

        session.Stdin.Feed(Msg.Download("d1"));
        session.Stdin.Feed(Msg.Download("d2"));

        var seen = await session.Stdout.WaitForAsync(2);
        seen[0].Should().BeOfType<IpcError>().Which.Should().Match<IpcError>(e => e.Id == "d1" && e.Code == code);
        seen[1].Should().BeOfType<Ack>().Which.Id.Should().Be("d2");
    }

    [Fact]
    public async Task When_Kasi_closes_the_pipe_the_host_sends_one_APP_NOT_RUNNING_error_and_exits_without_starting_Kasi_again()
    {
        var connector = new FakeConnector();
        var pipe = Server();
        await using var session = new RelaySession(connector.Then(pipe).Then(Server()));
        session.Stdin.Feed(Msg.Hello("h1"));
        await session.Stdout.WaitForAsync(1);

        pipe.Close();

        (await session.ExitCodeAsync()).Should().Be(HostRelay.ExitPipeLost);
        session.Stdout.Messages().Skip(1).Should().ContainSingle().Which.Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.AppNotRunning);
        connector.Connects.Should().Be(1);
    }

    [Fact]
    public async Task A_send_on_a_pipe_that_has_died_reconnects_once_and_delivers()
    {
        var dead = Server();
        var alive = Server();
        await using var session = new RelaySession(new FakeConnector().Then(dead).Then(alive));
        session.Stdin.Feed(Msg.Download("d0"));
        await session.Stdout.WaitForAsync(1);
        dead.Broken = true;

        session.Stdin.Feed(Msg.Download("d1"));

        (await session.Stdout.WaitForAsync(2))[1].Id.Should().Be("d1");
        alive.Sent.Select(m => m.Id).Should().Equal("d1");
    }

    [Fact]
    public async Task Nothing_but_whole_frames_reaches_stdout_and_the_diagnostics_go_to_the_log()
    {
        await using var session = new RelaySession(new FakeConnector().Then(Server()));

        session.Stdin.Feed(Msg.Hello("h1"));
        await session.Stdout.WaitForAsync(1); // the exit below must not race the answer
        session.Stdin.Feed("garbage");
        session.Stdin.Feed(TestStdin.Header(0));

        (await session.ExitCodeAsync()).Should().Be(HostRelay.ExitFraming);
        session.Stdout.Messages().Should().HaveCount(2); // throws if any byte is not part of a frame
        session.Log.ToString().Should().Contain("host:");
    }
}
