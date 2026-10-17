using System.Globalization;
using System.Text;

namespace Kasi.TestServer.Endpoints;

/// <summary>
/// Prefix routes that wrap another path: <c>/redirect/{hops}/…</c>, <c>/auth/basic/…</c>, <c>/auth/cookie/…</c>
/// and <c>/signed/…?exp=</c>. When the gate lets a request through it rewrites the path to the remainder, so
/// gates nest (<c>/redirect/2/signed/files/a/10?exp=…</c>).
/// </summary>
public static class PathGates
{
    public const string BasicUser = "kasi";
    public const string BasicPassword = "secret";
    public const string SessionCookie = "kasi_session";

    private enum Outcome
    {
        NotAGate,
        Rewritten,
        Responded,
    }

    internal static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        Outcome outcome;
        do
        {
            outcome = TryHandle(context);
        }
        while (outcome == Outcome.Rewritten);

        if (outcome == Outcome.NotAGate)
        {
            await next(context);
        }
    }

    private static Outcome TryHandle(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/redirect", out var redirect))
        {
            return Redirect(context, redirect);
        }

        if (path.StartsWithSegments("/auth/basic", out var basic))
        {
            return Basic(context, basic);
        }

        if (path.StartsWithSegments("/auth/cookie", out var cookie))
        {
            return Cookie(context, cookie);
        }

        return path.StartsWithSegments("/signed", out var signed) ? Signed(context, signed) : Outcome.NotAGate;
    }

    private static Outcome Redirect(HttpContext context, PathString rest)
    {
        var parts = rest.Value?.TrimStart('/').Split('/', 2);
        if (parts is not { Length: 2 } || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hops))
        {
            return Respond(context, StatusCodes.Status400BadRequest);
        }

        if (hops == 0)
        {
            return Unwrap(context, parts[1]);
        }

        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = $"/redirect/{hops - 1}/{parts[1]}{context.Request.QueryString}";
        return Outcome.Responded;
    }

    private static Outcome Basic(HttpContext context, PathString rest)
    {
        if (!TryReadBasicCredentials(context, out var user, out var password) || user != BasicUser || password != BasicPassword)
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"kasi\"";
            return Respond(context, StatusCodes.Status401Unauthorized);
        }

        return Unwrap(context, rest.Value);
    }

    private static Outcome Cookie(HttpContext context, PathString rest)
    {
        if (rest == "/login")
        {
            context.Response.Headers.SetCookie = $"{SessionCookie}=ok; Path=/; HttpOnly";
            return Respond(context, StatusCodes.Status204NoContent);
        }

        return context.Request.Cookies[SessionCookie] == "ok" ? Unwrap(context, rest.Value) : Respond(context, StatusCodes.Status401Unauthorized);
    }

    private static Outcome Signed(HttpContext context, PathString rest)
    {
        var time = context.RequestServices.GetRequiredService<TimeProvider>();
        var valid = long.TryParse(context.Request.Query["exp"], NumberStyles.None, CultureInfo.InvariantCulture, out var exp)
            && exp > time.GetUtcNow().ToUnixTimeSeconds();
        return valid ? Unwrap(context, rest.Value) : Respond(context, StatusCodes.Status403Forbidden);
    }

    private static Outcome Unwrap(HttpContext context, string? target)
    {
        context.Request.Path = new PathString("/" + (target ?? string.Empty).TrimStart('/'));
        return Outcome.Rewritten;
    }

    private static Outcome Respond(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return Outcome.Responded;
    }

    private static bool TryReadBasicCredentials(HttpContext context, out string user, out string password)
    {
        user = password = string.Empty;
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return false;
            }

            user = decoded[..colon];
            password = decoded[(colon + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
