using System.Net;
using Kasi.TestServer.Chaos;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Kasi.TestServer;

/// <summary>The test server running in-process on a free loopback port; dispose it at the end of the test.</summary>
public sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestServerHost(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
        Chaos = app.Services.GetRequiredService<ChaosRegistry>();
        Mutable = app.Services.GetRequiredService<MutableFiles>();
    }

    public Uri BaseAddress { get; }

    public ChaosRegistry Chaos { get; }

    public MutableFiles Mutable { get; }

    public static async Task<TestServerHost> StartAsync(TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        var app = TestServerApp.Create(IPAddress.Loopback, 0, time);
        await app.StartAsync(cancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestServerHost(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
