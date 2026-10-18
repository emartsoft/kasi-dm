using System.Net;
using System.Security.Cryptography.X509Certificates;
using Kasi.TestServer.Chaos;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Kasi.TestServer;

/// <summary>The test server running in-process on a free loopback port; dispose it at the end of the test.</summary>
public sealed class TestServerHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TestServerCertificates _certificates;

    private TestServerHost(WebApplication app, Uri baseAddress, TestServerMetrics metrics, TestServerCertificates certificates)
    {
        _app = app;
        BaseAddress = baseAddress;
        Metrics = metrics;
        _certificates = certificates;
        Chaos = app.Services.GetRequiredService<ChaosRegistry>();
        Mutable = app.Services.GetRequiredService<MutableFiles>();
    }

    public Uri BaseAddress { get; }

    public ChaosRegistry Chaos { get; }

    public MutableFiles Mutable { get; }

    public TestServerMetrics Metrics { get; }

    /// <summary>The ephemeral root that signed the valid, expired and wrong-host leaves; null when TLS is off.</summary>
    public X509Certificate2? RootCertificate => _certificates.RootCertificate;

    public void ResetMetrics() => Metrics.Reset();

    /// <summary>Starts a plain HTTP/1.1 server, the behaviour every existing test expects.</summary>
    public static Task<TestServerHost> StartAsync(TimeProvider? time = null, CancellationToken cancellationToken = default) =>
        StartAsync(new TestServerOptions(), time, cancellationToken);

    public static async Task<TestServerHost> StartAsync(
        TestServerOptions options,
        TimeProvider? time = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var certificates = TestServerCertificates.Create(options.TlsMode);
        var metrics = new TestServerMetrics();
        WebApplication? app = null;
        try
        {
            app = TestServerApp.Create(IPAddress.Loopback, 0, time, options, metrics, certificates.ServerCertificate);
            await app.StartAsync(cancellationToken);
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new TestServerHost(app, new Uri(address), metrics, certificates);
        }
        catch
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }

            certificates.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();
            _certificates.Dispose();
        }
    }
}
