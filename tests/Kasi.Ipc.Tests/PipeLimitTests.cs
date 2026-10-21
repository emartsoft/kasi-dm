using System.Buffers.Binary;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Microsoft.Extensions.Time.Testing;
using static Kasi.Ipc.Tests.PipeTestSupport;

namespace Kasi.Ipc.Tests;

[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
public class PipeLimitTests
{
    private static byte[] Header(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        return header;
    }

    /// <summary>After the bad connection the server must still serve a good one.</summary>
    private static async Task ServerStillWorksAsync(string name, CancellationToken ct)
    {
        await using var client = await KasiPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await client.SendAsync(Hello("after"), ct);
        (await client.ReceiveAsync(ct)).Should().BeOfType<Rules>().Which.Id.Should().Be("after");
    }

    [Theory]
    [InlineData(0u)]
    [InlineData((uint)IpcFraming.MaxFrameBytes + 1)]
    [InlineData(uint.MaxValue)]
    public async Task A_frame_length_of_zero_or_over_the_cap_closes_the_connection(uint length)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        await using var raw = await RawClientAsync(name, ct);

        await WriteRawAsync(raw, Header(length), ct);

        (await IsClosedByServerAsync(raw, ct)).Should().BeTrue();
        await ServerStillWorksAsync(name, ct);
    }

    [Fact]
    public async Task A_truncated_frame_closes_the_connection_when_the_peer_leaves()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        var raw = await RawClientAsync(name, ct);
        await WriteRawAsync(raw, [.. Header(100), .. new byte[10]], ct);

        await raw.DisposeAsync();

        await ServerStillWorksAsync(name, ct);
        await WaitUntilAsync(() => server.Connections.Count == 1 || server.Connections.Count == 0, ct);
    }

    [Fact]
    public async Task Garbage_bytes_get_an_invalid_message_error_and_a_closed_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var seen = new List<IpcMessage>();
        await using var server = StartServer(name, seen);
        await using var raw = await RawClientAsync(name, ct);
        var garbage = "this is not json at all"u8.ToArray();

        await WriteRawAsync(raw, [.. Header((uint)garbage.Length), .. garbage], ct);
        var replies = await ReadAllAsync(raw, ct);

        replies.Should().ContainSingle().Which.Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.InvalidMessage);
        seen.Should().BeEmpty("no handler ever sees a message that failed validation");
        await ServerStillWorksAsync(name, ct);
    }

    [Fact]
    public async Task A_first_message_that_is_not_a_hello_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var seen = new List<IpcMessage>();
        await using var server = StartServer(name, seen);
        await using var raw = await RawClientAsync(name, ct);

        await IpcFraming.WriteAsync(raw, IpcSerializer.Serialize(Download("early")), ct);
        var replies = await ReadAllAsync(raw, ct);

        replies.Should().ContainSingle().Which.Should().BeOfType<IpcError>().Which.Should().BeEquivalentTo(new { Id = "early", Code = IpcErrorCode.InvalidMessage });
        seen.Should().BeEmpty();
        server.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task A_hello_whose_versions_exclude_1_gets_unsupported_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        await using var raw = await RawClientAsync(name, ct);

        await IpcFraming.WriteAsync(raw, IpcSerializer.Serialize(Hello() with { ProtocolMin = 2, ProtocolMax = 3 }), ct);
        var replies = await ReadAllAsync(raw, ct);

        replies.Should().ContainSingle().Which.Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.UnsupportedVersion);
        server.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_of_version_2_gets_unsupported_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        await using var raw = await RawClientAsync(name, ct);
        var future = """{"v":2,"id":"x","type":"hello","somethingNew":true}"""u8.ToArray();

        await WriteRawAsync(raw, [.. Header((uint)future.Length), .. future], ct);
        var replies = await ReadAllAsync(raw, ct);

        replies.Should().ContainSingle().Which.Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.UnsupportedVersion);
    }

    [Fact]
    public async Task A_connection_that_never_says_hello_is_closed_after_the_timeout()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var clock = new FakeTimeProvider();
        await using var server = StartServer(name, options: new KasiPipeServerOptions { HelloTimeout = TimeSpan.FromSeconds(10) }, time: clock);
        await using var raw = await RawClientAsync(name, ct);
        await Task.Delay(100, ct);

        clock.Advance(TimeSpan.FromSeconds(11));

        (await IsClosedByServerAsync(raw, ct)).Should().BeTrue();
        await ServerStillWorksAsync(name, ct);
    }

    [Fact]
    public async Task More_connections_than_the_cap_are_refused_and_the_rest_keep_working()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name, options: new KasiPipeServerOptions { MaxConnections = 2 });
        await using var first = await KasiPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await using var second = await KasiPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await first.SendAsync(Hello("a"), ct);
        await second.SendAsync(Hello("b"), ct);
        await first.ReceiveAsync(ct);
        await second.ReceiveAsync(ct);

        await using var third = await RawClientAsync(name, ct);

        (await IsClosedByServerAsync(third, ct)).Should().BeTrue();
        server.Connections.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_host_that_does_not_take_a_broadcast_is_dropped_and_the_others_still_get_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var clock = new FakeTimeProvider();
        await using var server = StartServer(name, options: new KasiPipeServerOptions { BroadcastTimeout = TimeSpan.FromSeconds(5) }, time: clock);
        await using var stuck = await KasiPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await using var healthy = await KasiPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5), ct);
        await stuck.SendAsync(Hello("a"), ct);
        await healthy.SendAsync(Hello("b"), ct);
        await stuck.ReceiveAsync(ct);
        await healthy.ReceiveAsync(ct);

        // far larger than the pipe buffers, so the write to a host that never reads cannot finish
        var big = Rules("big") with { Extensions = [.. Enumerable.Repeat(new string('x', 250), 1_000)] };
        var broadcast = server.BroadcastAsync(big, ct);
        var received = await healthy.ReceiveAsync(ct);
        await Task.Delay(100, ct);
        clock.Advance(TimeSpan.FromSeconds(6));

        (await broadcast).Should().Be(1);
        received.Should().BeOfType<Rules>().Which.Id.Should().Be("big");
        await WaitUntilAsync(() => server.Connections.Count == 1, ct);
    }
}
