namespace Kasi.TestServer.Chaos;

/// <summary>Applies the profile named by <c>X-Kasi-Chaos</c>: latency, connection cap and injected statuses here, byte faults via <see cref="ChaosStream"/>.</summary>
internal sealed class ChaosMiddleware(RequestDelegate next, ChaosRegistry registry, ConnectionTracker connections)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(ChaosRegistry.HeaderName, out var header)
            || context.Request.Path.StartsWithSegments("/_chaos", StringComparison.Ordinal))
        {
            await next(context);
            return;
        }

        var session = registry.Begin(header.ToString());
        if (session is null)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync($"Unknown chaos profile '{header}'.", context.RequestAborted);
            return;
        }

        context.Items[ChaosSession.ItemKey] = session;
        var profile = session.Profile;

        if (profile.LatencyMs > 0 || profile.LatencyJitterMs > 0)
        {
            var delay = profile.LatencyMs + (profile.LatencyJitterMs > 0 ? session.Rng.Next(0, profile.LatencyJitterMs + 1) : 0);
            await Task.Delay(delay, context.RequestAborted);
        }

        var connection = connections.Get(context.Connection.Id);
        if (profile.MaxConnectionsPerClient is { } max && connection?.ClientOrdinal > max)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (profile.StatusCode is { } status && session.Rng.NextDouble() < profile.StatusRate)
        {
            context.Response.StatusCode = status;
            if (profile.RetryAfterSeconds is { } retryAfter)
            {
                context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            return;
        }

        if (profile.ResetAfterBytes <= 0)
        {
            session.Aborted = true;
            context.Abort();
            return;
        }

        var original = context.Response.Body;
        context.Response.Body = new ChaosStream(original, context, session, connection);
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (session.Aborted)
        {
            // the reset fault ended the connection on purpose
        }
        finally
        {
            context.Response.Body = original;
        }
    }
}
