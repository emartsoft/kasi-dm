using System.Net;
using Kasi.TestServer.Chaos;
using Kasi.TestServer.Endpoints;
using Microsoft.AspNetCore.Connections;

namespace Kasi.TestServer;

internal static class TestServerApp
{
    public static WebApplication Create(IPAddress address, int port, TimeProvider? time = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();

        var tracker = new ConnectionTracker();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(address, port, listen => listen.Use(tracker.TrackAsync));
        });
        builder.Services.AddSingleton(tracker);
        builder.Services.AddSingleton<ChaosRegistry>();
        builder.Services.AddSingleton<MutableFiles>();
        builder.Services.AddSingleton(time ?? TimeProvider.System);

        var app = builder.Build();
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
}
