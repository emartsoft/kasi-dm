using System.Net;
using System.Security.Cryptography.X509Certificates;
using Kasi.TestServer.Chaos;
using Kasi.TestServer.Endpoints;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Kasi.TestServer;

internal static class TestServerApp
{
    public static WebApplication Create(
        IPAddress address,
        int port,
        TimeProvider? time = null,
        TestServerOptions? options = null,
        TestServerMetrics? metrics = null,
        X509Certificate2? serverCertificate = null)
    {
        options ??= new TestServerOptions();
        metrics ??= new TestServerMetrics();
        if (options.TlsMode != TestServerTlsMode.None && serverCertificate is null)
        {
            throw new InvalidOperationException("A server certificate is required when TLS is enabled.");
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var tracker = new ConnectionTracker(metrics);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(address, port, listen =>
            {
                listen.Protocols = ToProtocols(options.EffectiveProtocolMode);
                listen.Use(tracker.TrackAsync);

                if (serverCertificate is not null)
                {
                    listen.UseHttps(serverCertificate);
                }
            });
        });
        builder.Services.AddSingleton(tracker);
        builder.Services.AddSingleton(metrics);
        builder.Services.AddSingleton<ChaosRegistry>();
        builder.Services.AddSingleton<MutableFiles>();
        builder.Services.AddSingleton(time ?? TimeProvider.System);

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            metrics.RequestServed(context);
            try
            {
                await next(context);
            }
            finally
            {
                var chaosAbort = context.Items.TryGetValue(ChaosSession.ItemKey, out var session)
                    && session is ChaosSession { Aborted: true };
                if (context.RequestAborted.IsCancellationRequested && !chaosAbort)
                {
                    metrics.RequestAborted();
                }
            }
        });

        app.UseMiddleware<ChaosMiddleware>();
        app.Use(PathGates.InvokeAsync);
        app.UseRouting();

        FileEndpoints.Map(app);
        DispositionEndpoints.Map(app);
        StreamingEndpoints.Map(app);

        app.MapPost("/_chaos", (ChaosProfile profile, ChaosRegistry registry) => Results.Ok(new { id = registry.Register(profile) }));
        app.MapDelete("/_chaos", (ChaosRegistry registry) =>
        {
            registry.Clear();
            return Results.NoContent();
        });

        return app;
    }

    private static HttpProtocols ToProtocols(TestServerProtocolMode mode) => mode switch
    {
        TestServerProtocolMode.Http1 => HttpProtocols.Http1,
        TestServerProtocolMode.Http2 => HttpProtocols.Http2,
        TestServerProtocolMode.Http1AndHttp2 => HttpProtocols.Http1AndHttp2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown protocol mode."),
    };
}
