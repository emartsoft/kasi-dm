using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using static Kasi.Ipc.Tests.PipeTestSupport;

namespace Kasi.Ipc.Tests;

[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
public class PipeRoundTripTests
{
    private static readonly TimeSpan _connect = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Hello_is_answered_with_rules_and_a_download_with_an_ack()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var seen = new List<IpcMessage>();
        await using var server = StartServer(name, seen);
        await using var client = await KasiPipeClient.ConnectAsync(name, _connect, ct);

        await client.SendAsync(Hello(), ct);
        var rules = await client.ReceiveAsync(ct);
        await client.SendAsync(Download("d9"), ct);
        var ack = await client.ReceiveAsync(ct);

        rules.Should().BeOfType<Rules>().Which.Id.Should().Be("h1");
        ack.Should().BeOfType<Ack>().Which.Id.Should().Be("d9");
        seen.Select(m => m.GetType()).Should().Equal(typeof(Hello), typeof(AddDownload));
        server.Connections.Should().ContainSingle().Which.Client.Should().Be(BrowserClient.Chrome);
    }

    [Fact]
    public async Task Many_hosts_connect_at_once_and_a_push_can_go_to_one_or_to_all()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        var clients = new List<KasiPipeClient>();
        try
        {
            foreach (var kind in new[] { BrowserClient.Chrome, BrowserClient.Edge, BrowserClient.Firefox })
            {
                var client = await KasiPipeClient.ConnectAsync(name, _connect, ct);
                clients.Add(client);
                await client.SendAsync(Hello("h-" + kind, kind), ct);
                (await client.ReceiveAsync(ct)).Should().BeOfType<Rules>();
            }

            server.Connections.Select(c => c.Client).Should().BeEquivalentTo([BrowserClient.Chrome, BrowserClient.Edge, BrowserClient.Firefox]);

            var edge = server.Connections.Single(c => c.Client == BrowserClient.Edge);
            await server.SendAsync(edge, Rules("push-one"), ct);
            (await clients[1].ReceiveAsync(ct)).Should().BeOfType<Rules>().Which.Id.Should().Be("push-one");

            var delivered = await server.BroadcastAsync(Rules("push-all"), ct);
            delivered.Should().Be(3);
            foreach (var client in clients)
            {
                (await client.ReceiveAsync(ct)).Should().BeOfType<Rules>().Which.Id.Should().Be("push-all");
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                await client.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_request_to_a_host_completes_with_the_ack_that_carries_its_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        await using var client = await KasiPipeClient.ConnectAsync(name, _connect, ct);
        await client.SendAsync(Hello(), ct);
        await client.ReceiveAsync(ct);
        var connection = server.Connections.Single();
        var refresh = new RefreshLink { Id = "refresh-1", JobId = "job", ReferrerUrl = "https://example.com/page" };

        var request = connection.RequestAsync(refresh, TimeSpan.FromSeconds(5), ct);
        (await client.ReceiveAsync(ct)).Should().BeOfType<RefreshLink>().Which.Id.Should().Be("refresh-1");
        await client.SendAsync(new Ack { Id = "refresh-1", Ok = true }, ct);

        (await request).Should().BeOfType<Ack>().Which.Id.Should().Be("refresh-1");
    }

    [Fact]
    public async Task A_request_the_host_never_answers_times_out()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        await using var client = await KasiPipeClient.ConnectAsync(name, _connect, ct);
        await client.SendAsync(Hello(), ct);
        await client.ReceiveAsync(ct);

        var act = async () => await server.Connections.Single().RequestAsync(Rules("q"), TimeSpan.FromMilliseconds(200), ct);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task A_host_that_leaves_is_removed_from_the_registry_and_the_client_sees_a_closed_pipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        var client = await KasiPipeClient.ConnectAsync(name, _connect, ct);
        await client.SendAsync(Hello(), ct);
        await client.ReceiveAsync(ct);
        server.Connections.Should().HaveCount(1);

        await client.DisposeAsync();

        await WaitUntilAsync(() => server.Connections.Count == 0, ct);
    }

    [Fact]
    public async Task The_client_learns_when_the_server_goes_away()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var server = StartServer(name);
        await using var client = await KasiPipeClient.ConnectAsync(name, _connect, ct);
        var closed = 0;
        client.Closed += () => Interlocked.Increment(ref closed);
        await client.SendAsync(Hello(), ct);
        await client.ReceiveAsync(ct);

        await server.DisposeAsync();

        (await client.ReceiveAsync(ct)).Should().BeNull();
        closed.Should().Be(1);
    }

    [Fact]
    public async Task A_handler_that_throws_costs_the_message_an_error_reply_and_not_the_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = new KasiPipeServer(
            name,
            (_, message, _) => message is AddDownload ? throw new InvalidOperationException("https://example.com/?token=s3cr3t") : ValueTask.FromResult<IpcMessage?>(new Ack { Id = message.Id, Ok = true }));
        server.Start();
        await using var client = await KasiPipeClient.ConnectAsync(name, _connect, ct);
        await client.SendAsync(Hello(), ct);
        await client.ReceiveAsync(ct);

        await client.SendAsync(Download("boom"), ct);
        var error = await client.ReceiveAsync(ct);
        await client.SendAsync(OpenSettingsMessage(), ct);
        var next = await client.ReceiveAsync(ct);

        error.Should().BeOfType<IpcError>().Which.Should().BeEquivalentTo(new { Id = "boom", Code = IpcErrorCode.Internal, Detail = (string?)null });
        next.Should().BeOfType<Ack>();
    }

    private static OpenSettings OpenSettingsMessage() => new() { Id = "o1" };
}
