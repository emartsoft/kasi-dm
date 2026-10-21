using System.Diagnostics;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.NativeHost.Tests;

/// <summary>
/// TC-BRW-006 across the process boundary: the published Native AOT executable, started like the browser starts it, with real stdin and
/// stdout pipes. It needs the publish output, so it runs where <c>KASI_NATIVEHOST_AOT</c> names the executable (the CI job does); without
/// it the test is skipped, and <c>KASI_REQUIRE_AOT=1</c> turns the skip into a failure.
/// </summary>
[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
[Trait("Category", "Integration")]
[Trait("Category", "AotSample")]
public sealed class AotExecutableTests
{
    private const int Sample = 1_000;
    private const int Seed = 20261021;

    [Fact]
    public async Task The_published_executable_refuses_a_thousand_malformed_messages_over_real_pipes_and_exits_cleanly()
    {
        var exe = Executable();
        using var host = Start(exe);
        var replies = ReadRepliesAsync(host.StandardOutput.BaseStream);
        var stderr = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        var stdin = host.StandardInput.BaseStream;
        var sent = 0;
        try
        {
            for (; sent < Sample; sent++)
            {
                await stdin.WriteAsync(TestStdin.Frame(MalformedFrames.Make(Seed, sent)), TestContext.Current.CancellationToken);
            }

            await stdin.FlushAsync(TestContext.Current.CancellationToken);
        }
        catch (IOException)
        {
            // the host stopped reading: say why instead of the broken pipe
            await host.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Fail($"The host ended after {sent} frames (frame {sent} was MalformedFrames.Make({Seed}, {sent})) with exit code {host.ExitCode}: {await stderr}");
        }

        host.StandardInput.Close(); // the browser went away
        await host.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        host.ExitCode.Should().Be(0);
        var seen = await replies;
        seen.Should().HaveCount(Sample).And.OnlyContain(m => m is IpcError && ((IpcError)m).Code != IpcErrorCode.AppNotRunning);
        (await stderr).Should().BeEmpty("a refused message is not logged");
    }

    [Fact]
    public async Task The_published_executable_exits_with_code_2_and_writes_nothing_on_a_broken_frame()
    {
        var exe = Executable();
        using var host = Start(exe);
        var replies = ReadRepliesAsync(host.StandardOutput.BaseStream);
        var stderr = host.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        await host.StandardInput.BaseStream.WriteAsync(TestStdin.Header(0), TestContext.Current.CancellationToken);
        await host.StandardInput.BaseStream.FlushAsync(TestContext.Current.CancellationToken);
        await host.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        host.ExitCode.Should().Be(2);
        (await replies).Should().BeEmpty();
        (await stderr).Should().Contain("broken frame");
    }

    private static string Executable()
    {
        var path = Environment.GetEnvironmentVariable("KASI_NATIVEHOST_AOT");
        if (string.IsNullOrEmpty(path))
        {
            Assert.SkipWhen(Environment.GetEnvironmentVariable("KASI_REQUIRE_AOT") != "1", "KASI_NATIVEHOST_AOT does not name the published Kasi.NativeHost.exe.");
            Assert.Fail("KASI_REQUIRE_AOT is set but KASI_NATIVEHOST_AOT does not name the published Kasi.NativeHost.exe.");
        }

        File.Exists(path).Should().BeTrue($"{path} must be the published executable");
        return path;
    }

    private static Process Start(string exe) => Process.Start(new ProcessStartInfo(exe)
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    })!;

    private static async Task<List<IpcMessage>> ReadRepliesAsync(Stream stdout)
    {
        List<IpcMessage> messages = [];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (await IpcFraming.ReadAsync(stdout, timeout.Token) is { } frame)
        {
            messages.Add(IpcSerializer.Deserialize(frame));
        }

        return messages;
    }
}
