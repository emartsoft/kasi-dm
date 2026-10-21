using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using static Kasi.Ipc.Tests.PipeTestSupport;

namespace Kasi.Ipc.Tests;

[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
[Trait("Requirement", "KASI-BRW-006")]
public class PipeSecurityTests
{
    private static SecurityIdentifier Sid(WellKnownSidType type) => new(type, null);

    [Fact]
    public void The_pipe_name_is_kasi_and_the_users_sid()
    {
        var sid = KasiPipeNames.CurrentUser();

        KasiPipeNames.ForCurrentUser().Should().Be("kasi-" + sid.Value).And.StartWith("kasi-S-1-5-");
        KasiPipeNames.For(new SecurityIdentifier("S-1-5-21-1-2-3-1001")).Should().Be("kasi-S-1-5-21-1-2-3-1001");
    }

    [Fact]
    public async Task The_access_list_of_a_created_pipe_has_the_users_allow_and_the_network_deny_and_nothing_else()
    {
        await using var server = StartServer(NewPipeName());
        var user = KasiPipeNames.CurrentUser();

        var rules = server.ReadSecurity().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        rules.Should().HaveCount(2, "one allow for the user and one deny for the network, nothing for anyone else");
        var allow = rules.Single(r => r.AccessControlType == AccessControlType.Allow);
        allow.IdentityReference.Should().Be(user);
        allow.PipeAccessRights.Should().HaveFlag(PipeAccessRights.ReadWrite).And.HaveFlag(PipeAccessRights.CreateNewInstance);
        allow.PipeAccessRights.Should().NotHaveFlag(PipeAccessRights.ChangePermissions).And.NotHaveFlag(PipeAccessRights.TakeOwnership);
        var deny = rules.Single(r => r.AccessControlType == AccessControlType.Deny);
        deny.IdentityReference.Should().Be(Sid(WellKnownSidType.NetworkSid));
        rules.Select(r => r.IdentityReference).Should().NotContain(
            [Sid(WellKnownSidType.WorldSid), Sid(WellKnownSidType.BuiltinUsersSid), Sid(WellKnownSidType.AuthenticatedUserSid), Sid(WellKnownSidType.AnonymousSid)]);
    }

    [Fact]
    public async Task The_pipe_is_owned_by_the_user_so_a_current_user_only_client_accepts_it()
    {
        await using var server = StartServer(NewPipeName());

        server.ReadSecurity().GetOwner(typeof(SecurityIdentifier)).Should().Be(KasiPipeNames.CurrentUser());
    }

    [Fact]
    public async Task A_second_server_with_the_same_name_fails_instead_of_sharing_the_pipe()
    {
        var name = NewPipeName();
        await using var first = StartServer(name);
        await using var second = new KasiPipeServer(name, (_, _, _) => ValueTask.FromResult<IpcMessage?>(null));

        var act = second.Start;

        var thrown = act.Should().Throw<Exception>().Which;
        thrown.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
    }

    [Fact]
    public async Task A_client_refuses_a_server_owned_by_another_user_and_sends_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        var seen = new List<IpcMessage>();
        await using var server = StartServer(name, seen);
        var stranger = new SecurityIdentifier("S-1-5-21-111-222-333-1001");

        var act = async () => await KasiPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5), _ => stranger, ct);

        await act.Should().ThrowAsync<IpcServerNotTrustedException>();
        await Task.Delay(200, ct);
        seen.Should().BeEmpty("the client must not send anything to a server it does not trust");
        server.Connections.Should().BeEmpty();
    }

    [Fact]
    public async Task A_client_refuses_a_server_whose_owner_cannot_be_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);

        var act = async () => await KasiPipeClient.ConnectAsync(name, TimeSpan.FromSeconds(5), _ => null, ct);

        await act.Should().ThrowAsync<IpcServerNotTrustedException>("failing closed beats trusting an unknown owner");
    }

    [Fact]
    public async Task A_client_finds_this_process_to_be_owned_by_the_current_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = NewPipeName();
        await using var server = StartServer(name);
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000, ct);

        ServerIdentity.OwnerOfServer(pipe).Should().Be(KasiPipeNames.CurrentUser());
    }

    [Fact]
    public async Task Connecting_to_a_name_nobody_serves_times_out()
    {
        var act = async () => await KasiPipeClient.ConnectAsync(NewPipeName(), TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<TimeoutException>();
    }
}
