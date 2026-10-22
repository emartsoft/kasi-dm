using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using Kasi.Ipc.Transport;
using static Kasi.Ipc.Tests.PipeTestSupport;

namespace Kasi.Ipc.Tests;

/// <summary>
/// The pipe has to refuse another user on the same machine (TC-BRW-006, plan.md M2.1). This makes a temporary local user, runs a
/// process as that user, and has it open the Kasi pipe; a control pipe that allows everyone proves the process can open pipes at all,
/// so a refusal means something. It needs Administrator (CI is elevated; set <c>KASI_REQUIRE_ADMIN=1</c> there so a skip is a failure).
/// </summary>
[Trait("Category", "Integration")]
[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
public class CrossUserPipeTests
{
    [Fact]
    public async Task Another_local_user_cannot_open_the_pipe_but_can_open_one_that_allows_everyone()
    {
        var ct = TestContext.Current.CancellationToken;
        if (!IsAdministrator())
        {
            Assert.True(Environment.GetEnvironmentVariable("KASI_REQUIRE_ADMIN") != "1", "KASI_REQUIRE_ADMIN=1 but this run is not elevated.");
            Assert.Skip("Creating a local user needs Administrator.");
        }

        var account = "kasiipc" + Guid.NewGuid().ToString("N")[..8];
        // net user asks a question when a password is longer than 14 characters, so keep it short
        var password = "Kp!" + Guid.NewGuid().ToString("N")[..6] + "A1";
        var kasiName = NewPipeName();
        var controlName = NewPipeName();
        var handOffName = NewPipeName();
        await using var server = StartServer(kasiName);
        await using var handOff = new HandOffServer(handOffName, (_, _) => ValueTask.CompletedTask);
        handOff.Start();
        using var control = NamedPipeServerStreamAcl.Create(
            controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, EveryoneSecurity());

        await RunAsync("net", ["user", account, password, "/add"], null, ct);
        try
        {
            var script =
                "function Try-Open($n) { try { $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $n, [System.IO.Pipes.PipeDirection]::InOut); " +
                "$c.Connect(3000); $c.Dispose(); 'CONNECTED' } catch [System.UnauthorizedAccessException] { 'DENIED' } " +
                "catch { 'OTHER:' + $_.Exception.GetType().Name } }; " +
                $"Write-Output ('control=' + (Try-Open '{controlName}')); Write-Output ('kasi=' + (Try-Open '{kasiName}')); Write-Output ('handoff=' + (Try-Open '{handOffName}'))";
            var credentials = new NetworkCredential(account, password, Environment.MachineName);

            var output = await RunAsync(
                Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                ["-NoProfile", "-NonInteractive", "-Command", script],
                credentials,
                ct);

            output.Should().Contain("control=CONNECTED", "the other user's process must be able to open a pipe that allows it, or the next line proves nothing");
            output.Should().Contain("kasi=DENIED", "only the owner may open the Kasi pipe");
            output.Should().Contain("handoff=DENIED", "the argument hand-off pipe is per-user too (ADR 0023)");
            server.Connections.Should().BeEmpty();
        }
        finally
        {
            await RunAsync("net", ["user", account, "/delete"], null, CancellationToken.None);
        }
    }

    private static PipeSecurity EveryoneSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static async Task<string> RunAsync(string file, string[] arguments, NetworkCredential? runAs, CancellationToken ct)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (runAs is not null)
        {
            start.UserName = runAs.UserName;
            start.Domain = runAs.Domain;
            start.Password = runAs.SecurePassword;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(limit.Token);
        var text = await output + await error;
        if (runAs is null)
        {
            process.ExitCode.Should().Be(0, text);
        }

        return text;
    }
}
