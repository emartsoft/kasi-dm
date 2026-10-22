using System.Diagnostics;
using Kasi.App.Startup;
using Kasi.App.ViewModels;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;

namespace Kasi.App.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-SYS-005")]
public class StartupTests
{
    private static readonly TimeSpan _connect = TimeSpan.FromSeconds(10);

    private static Hello Hello() =>
        new() { Id = "h1", Client = BrowserClient.Chrome, ExtensionVersion = "1.0.0", ProtocolMin = 1, ProtocolMax = 1 };

    [Fact]
    [Trait("TC", "TC-SYS-006")]
    public async Task The_browser_pipe_answers_hello_before_the_engine_is_built()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoints = TestEndpoints.New();
        using var release = new ManualResetEventSlim();
        var building = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var built = false;
        AppSession? session = null;
        var run = Task.Run(
            () => AppStartup.Run(
                [],
                endpoints,
                args =>
                {
                    building.SetResult();
                    release.Wait(); // the engine is "slow"
                    built = true;
                    return CompositionRoot.Build(args);
                },
                s =>
                {
                    session = s;
                    return 0;
                },
                browser: TestBrowser.New()),
            ct);
        try
        {
            await building.Task.WaitAsync(_connect, ct);

            await using var client = await KasiPipeClient.ConnectAsync(endpoints.BrowserPipe, _connect, ct);
            await client.SendAsync(Hello(), ct);
            var reply = await client.ReceiveAsync(ct);

            reply.Should().BeOfType<Rules>("the pipe serves while the host is still being built");
            built.Should().BeFalse();
        }
        finally
        {
            release.Set();
        }

        (await run).Should().Be(0);
        await session!.DisposeAsync();
    }

    [Fact]
    [Trait("TC", "TC-SYS-006")]
    public async Task The_browser_pipe_answers_hello_with_the_populated_default_rules_and_media_messages_get_an_error_until_m3()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoints = TestEndpoints.New();
        AppSession? session = null;
        using var done = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(
            () => AppStartup.Run(
                [],
                endpoints,
                CompositionRoot.Build,
                s =>
                {
                    session = s;
                    started.SetResult();
                    done.Wait();
                    return 0;
                },
                browser: TestBrowser.New()),
            ct);
        await started.Task.WaitAsync(_connect, ct);
        await using var client = await KasiPipeClient.ConnectAsync(endpoints.BrowserPipe, _connect, ct);

        await client.SendAsync(Hello(), ct);
        var rules = await client.ReceiveAsync(ct);
        await client.SendAsync(new CaptureMedia { Id = "m1", TabId = 1, Url = "https://example.com/a.m3u8", Kind = MediaKind.Hls, Cookies = [], Referrer = "https://example.com/", UserAgent = "t" }, ct);
        var error = await client.ReceiveAsync(ct);

        rules.Should().BeOfType<Rules>().Which.Should().Match<Rules>(r => r.Enabled && r.Extensions.Contains("zip") && r.MimeTypes.Count > 0);
        error.Should().BeOfType<IpcError>().Which.Should().Match<IpcError>(e => e.Id == "m1" && e.Code == IpcErrorCode.Internal);
        done.Set();
        (await run).Should().Be(0);
        await session!.DisposeAsync();
    }

    [Fact]
    [Trait("TC", "TC-SYS-006")]
    public async Task A_second_instance_in_the_same_session_hands_its_urls_over_and_exits_zero_without_building_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        var endpoints = TestEndpoints.New();
        AppSession? first = null;
        var gotUrl = new TaskCompletionSource<AppArguments>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var done = new ManualResetEventSlim();
        var run = Task.Run(
            () => AppStartup.Run(
                [],
                endpoints,
                CompositionRoot.Build,
                s =>
                {
                    first = s;
                    s.Relay.Attach(r => gotUrl.TrySetResult(r));
                    started.SetResult();
                    done.Wait();
                    return 0;
                },
                browser: TestBrowser.New()),
            ct);
        await started.Task.WaitAsync(_connect, ct);
        var secondBuilt = false;

        var exit = await Task.Run(
            () => AppStartup.Run(
                ["--tray", "https://example.com/second.zip"],
                endpoints,
                _ =>
                {
                    secondBuilt = true;
                    throw new InvalidOperationException("the second instance must not build a host");
                },
                _ => throw new InvalidOperationException("the second instance must not start a UI")),
            ct);

        exit.Should().Be(0);
        secondBuilt.Should().BeFalse();
        var request = await gotUrl.Task.WaitAsync(_connect, ct);
        request.Tray.Should().BeTrue();
        request.Urls.Should().Equal("https://example.com/second.zip");
        done.Set();
        (await run).Should().Be(0);
        await first!.DisposeAsync();
    }

    [Fact]
    [Trait("TC", "TC-SYS-006")]
    public async Task A_real_second_process_passes_its_url_to_the_first_instance_and_exits_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var exe = Path.Combine(AppContext.BaseDirectory, "Kasi.App.exe");
        if (!File.Exists(exe))
        {
            Assert.Skip($"Kasi.App.exe is not next to the tests ({AppContext.BaseDirectory}); nothing to start as the second process.");
        }

        var endpoints = TestEndpoints.New();
        var suffix = endpoints.InstanceLock[(endpoints.InstanceLock.LastIndexOf('-') + 1)..];
        const string Url = "https://example.com/second-process.zip?token=abc";
        MainWindowViewModel? viewModel = null;
        AppSession? first = null;
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var done = new ManualResetEventSlim();
        var run = Task.Run(
            () => AppStartup.Run(
                [],
                endpoints,
                CompositionRoot.Build,
                s =>
                {
                    first = s;
                    viewModel = s.Host.Services.GetRequiredService<MainWindowViewModel>();
                    s.Relay.Attach(r =>
                    {
                        viewModel.RequestAdd(r.Urls);
                        arrived.TrySetResult();
                    });
                    started.SetResult();
                    done.Wait();
                    return 0;
                },
                browser: TestBrowser.New()),
            ct);
        await started.Task.WaitAsync(_connect, ct);

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Url);
        start.Environment[AppEndpoints.TestSuffixVariable] = suffix;
        using var second = Process.Start(start)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(60)); // generous: a shared CI runner starts a .NET process slowly
        try
        {
            await second.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            second.Kill(entireProcessTree: true);
            throw new TimeoutException("The second Kasi.App process did not exit.");
        }

        second.ExitCode.Should().Be(0);
        await arrived.Task.WaitAsync(_connect, ct);
        viewModel!.PendingAddUrls.Should().Equal(Url);
        done.Set();
        (await run).Should().Be(0);
        await first!.DisposeAsync();
    }
}
