using Kasi.Ipc.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kasi.App.Startup;

/// <summary>What the UI step of <see cref="AppStartup.Run"/> gets: the built host and the relay to attach to. Disposing it closes both pipes (tests; the process end does it otherwise).</summary>
internal sealed class AppSession(IHost host, LaunchRelay relay, bool startHidden, KasiPipeServer browserPipe, HandOffServer handOffPipe) : IAsyncDisposable
{
    public IHost Host { get; } = host;

    public LaunchRelay Relay { get; } = relay;

    /// <summary>True for <c>--tray</c>: no window until someone asks for one.</summary>
    public bool StartHidden { get; } = startHidden;

    public async ValueTask DisposeAsync()
    {
        await browserPipe.DisposeAsync().ConfigureAwait(false);
        await handOffPipe.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// The start-up order of ADR 0023. The browser host gives up after 5 s of waiting for the pipe, so the pipe opens before anything slow:
/// instance lock, hand-off or exit, browser pipe, hand-off pipe, and only then the host and engine, and the UI. Synchronous because
/// <c>Main</c> is the STA thread Avalonia needs.
/// </summary>
internal static class AppStartup
{
    public static readonly TimeSpan HandOffTimeout = TimeSpan.FromSeconds(5);

    // CA2000: the pipes and the session are owned by the process from here on; they are closed when it ends, or by a test that disposes the session
#pragma warning disable CA2000
    public static int Run(
        string[] args,
        AppEndpoints endpoints,
        Func<string[], IHost> buildHost,
        Func<AppSession, int> runUi,
        ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var request = AppArguments.Parse(args);

        using var instance = InstanceLock.TryAcquire(endpoints.InstanceLock);
        if (instance is null)
        {
            // another Kasi of this user runs: tell it what was asked, and be gone (exit 0 whether or not it answered)
            HandOffClient.TrySend(endpoints.HandOffPipe, request.ToHandOff(), HandOffTimeout);
            return 0;
        }

        var relay = new LaunchRelay(logger);
        var browserPipe = new KasiPipeServer(endpoints.BrowserPipe, BrowserPipeHandler.Create());
        var handOffPipe = new HandOffServer(endpoints.HandOffPipe, relay.OnHandOffAsync);
        try
        {
            browserPipe.Start();
            handOffPipe.Start();
        }
        catch (IOException)
        {
            // something that is not this Kasi owns a pipe name: say nothing about it, a WinExe has no console
            return 1;
        }

        // the engine and the UI come last; a request that arrives meanwhile waits in the relay
        using var host = buildHost(args);
        host.Start();
        if (request.Urls.Count > 0)
        {
            relay.Submit(request);
        }

        return runUi(new AppSession(host, relay, request.Tray, browserPipe, handOffPipe));
    }
#pragma warning restore CA2000
}
