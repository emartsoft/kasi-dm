using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Kasi.Ipc.Transport;
using static Kasi.Ipc.Tests.PipeTestSupport;

namespace Kasi.Ipc.Tests;

[Trait("TC", "TC-SYS-006")]
[Trait("Requirement", "KASI-SYS-005")]
public class HandOffTests
{
    private static HandOffServer Start(string name, List<IReadOnlyList<string>> received, TimeSpan? readTimeout = null)
    {
        var server = new HandOffServer(
            name,
            (arguments, _) =>
            {
                lock (received)
                {
                    received.Add(arguments);
                }

                return ValueTask.CompletedTask;
            },
            readTimeout);
        server.Start();
        return server;
    }

    private static async Task SendRawAsync(string name, string text, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);
        await WriteRawAsync(pipe, Encoding.UTF8.GetBytes(text), ct);
    }

    [Fact]
    public void The_hand_off_pipe_name_is_the_browser_pipe_name_plus_open()
    {
        KasiPipeNames.HandOffForCurrentUser().Should().Be(KasiPipeNames.ForCurrentUser() + "-open");
    }

    [Fact]
    public async Task Arguments_sent_by_a_client_reach_the_handler()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var received = new List<IReadOnlyList<string>>();
        await using var server = Start(name, received);

        HandOffClient.TrySend(name, ["--tray", "https://example.com/a.zip?token=x"], TimeSpan.FromSeconds(5)).Should().BeTrue();

        await WaitUntilAsync(() => received.Count == 1, ct);
        received.Single().Should().Equal("--tray", "https://example.com/a.zip?token=x");
    }

    [Fact]
    public async Task Several_launches_in_a_row_each_arrive()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var received = new List<IReadOnlyList<string>>();
        await using var server = Start(name, received);

        for (var i = 0; i < 5; i++)
        {
            HandOffClient.TrySend(name, [$"https://example.com/{i}"], TimeSpan.FromSeconds(5)).Should().BeTrue();
        }

        await WaitUntilAsync(() => received.Count == 5, ct);
    }

    [Fact]
    public void A_client_with_nobody_serving_gives_up_and_says_so()
    {
        HandOffClient.TrySend(NewPipeName(), ["x"], TimeSpan.FromMilliseconds(300)).Should().BeFalse();
    }

    [Theory]
    [InlineData("not json\n")]
    [InlineData("[\"a\"]\n")]
    [InlineData("{\"args\":\"a\"}\n")]
    [InlineData("{\"args\":[1]}\n")]
    [InlineData("{\"nope\":[]}\n")]
    [InlineData("")]
    public async Task A_line_that_is_not_the_contract_is_dropped_and_the_server_keeps_serving(string line)
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var received = new List<IReadOnlyList<string>>();
        await using var server = Start(name, received);

        await SendRawAsync(name, line, ct);
        HandOffClient.TrySend(name, ["ok"], TimeSpan.FromSeconds(5)).Should().BeTrue();

        await WaitUntilAsync(() => received.Count == 1, ct);
        received.Single().Should().Equal("ok");
    }

    [Fact]
    public async Task A_line_longer_than_the_cap_and_a_list_longer_than_the_cap_are_dropped()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var received = new List<IReadOnlyList<string>>();
        await using var server = Start(name, received);

        var tooMany = "{\"args\":[" + string.Join(",", Enumerable.Repeat("\"a\"", HandOffServer.MaxArguments + 1)) + "]}\n";
        await SendRawAsync(name, tooMany, ct);
        HandOffClient.TrySend(name, [new string('a', HandOffServer.MaxLineBytes)], TimeSpan.FromSeconds(5)).Should().BeFalse("the client refuses to send a line over the cap");
        await SendRawAsync(name, "{\"args\":[\"" + new string('a', HandOffServer.MaxLineBytes) + "\"]}\n", ct);
        HandOffClient.TrySend(name, ["ok"], TimeSpan.FromSeconds(5)).Should().BeTrue();

        await WaitUntilAsync(() => received.Count == 1, ct);
        received.Single().Should().Equal("ok");
    }

    [Fact]
    public async Task A_client_that_never_finishes_its_line_is_cut_off_and_the_server_keeps_serving()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var received = new List<IReadOnlyList<string>>();
        await using var server = Start(name, received, readTimeout: TimeSpan.FromMilliseconds(200));
        await using var slow = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await slow.ConnectAsync(5000, ct);
        await WriteRawAsync(slow, Encoding.UTF8.GetBytes("{\"args\":[\"https://exa"), ct);

        HandOffClient.TrySend(name, ["ok"], TimeSpan.FromSeconds(5)).Should().BeTrue();

        await WaitUntilAsync(() => received.Count == 1, ct);
        received.Single().Should().Equal("ok");
    }

    [Fact]
    public async Task The_access_list_has_the_users_allow_and_the_network_deny_and_nothing_else()
    {
        await using var server = Start(NewPipeName(), []);
        var user = KasiPipeNames.CurrentUser();

        var rules = server.ReadSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        rules.Should().HaveCount(2, "one allow for the user and one deny for the network, nothing for anyone else");
        rules.Single(r => r.AccessControlType == AccessControlType.Allow).IdentityReference.Should().Be(user);
        rules.Single(r => r.AccessControlType == AccessControlType.Deny).IdentityReference.Should().Be(new SecurityIdentifier(WellKnownSidType.NetworkSid, null));
        server.ReadSecurity().GetOwner(typeof(SecurityIdentifier)).Should().Be(user);
    }

    [Fact]
    public async Task A_second_server_with_the_same_name_fails_instead_of_sharing_the_pipe()
    {
        var name = NewPipeName();
        await using var first = Start(name, []);
        await using var second = new HandOffServer(name, (_, _) => ValueTask.CompletedTask);

        var act = second.Start;

        act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
    }
}
