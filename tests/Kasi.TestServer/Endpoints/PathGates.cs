using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Kasi.TestServer.Endpoints;

/// <summary>
/// Prefix routes that wrap another path: <c>/redirect/{hops}/…</c>, <c>/auth/basic/…</c>, <c>/auth/digest/…</c>, <c>/auth/cookie/…</c>
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

        if (path == "/redirect-to")
        {
            return RedirectTo(context);
        }

        if (path.StartsWithSegments("/auth/basic", out var basic))
        {
            return Basic(context, basic);
        }

        if (path.StartsWithSegments("/auth/digest", out var digest))
        {
            return Digest(context, digest);
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
            context.RequestServices.GetRequiredService<TestServerMetrics>().AuthChallengeSent();
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"kasi\"";
            return Respond(context, StatusCodes.Status401Unauthorized);
        }

        return Unwrap(context, rest.Value);
    }

    private static Outcome Digest(HttpContext context, PathString rest)
    {
        var nonce = context.RequestServices.GetRequiredService<DigestNonce>().Value;
        if (!ValidDigest(context, nonce))
        {
            context.RequestServices.GetRequiredService<TestServerMetrics>().AuthChallengeSent();
            context.Response.Headers.WWWAuthenticate = $"Digest realm=\"kasi\", nonce=\"{nonce}\", qop=\"auth\", algorithm=MD5";
            return Respond(context, StatusCodes.Status401Unauthorized);
        }

        return Unwrap(context, rest.Value);
    }

    private static Outcome RedirectTo(HttpContext context)
    {
        var target = context.Request.Query["target"].ToString();
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return Respond(context, StatusCodes.Status400BadRequest);
        }

        context.Response.StatusCode = StatusCodes.Status302Found;
        context.Response.Headers.Location = uri.AbsoluteUri;
        return Outcome.Responded;
    }

    private static bool ValidDigest(HttpContext context, string nonce)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase) || header.Length > 4096)
        {
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in header["Digest ".Length..].Split(','))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                return false;
            }

            values[part[..separator].Trim()] = part[(separator + 1)..].Trim().Trim('"');
        }

        var uri = context.Request.Path + context.Request.QueryString;
        if (!values.TryGetValue("username", out var user) || user != BasicUser
            || !values.TryGetValue("realm", out var realm) || realm != "kasi"
            || !values.TryGetValue("nonce", out var suppliedNonce) || suppliedNonce != nonce
            || !values.TryGetValue("uri", out var suppliedUri) || suppliedUri != uri
            || !values.TryGetValue("qop", out var qop) || !qop.Equals("auth", StringComparison.OrdinalIgnoreCase)
            || !values.TryGetValue("nc", out var nc) || string.IsNullOrWhiteSpace(nc)
            || !values.TryGetValue("cnonce", out var cnonce) || string.IsNullOrWhiteSpace(cnonce)
            || !values.TryGetValue("response", out var suppliedResponse))
        {
            return false;
        }

        // RFC 7616 Digest's MD5 algorithm is used only in this local test fixture, never to protect stored credentials.
        var ha1 = Md5Hex($"{BasicUser}:kasi:{BasicPassword}");
        var ha2 = Md5Hex($"{context.Request.Method}:{uri}");
        var expected = Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
        return expected.Equals(suppliedResponse, StringComparison.OrdinalIgnoreCase);
    }

    private static string Md5Hex(string value)
    {
#pragma warning disable CA5351 // MD5 is mandated by the Digest challenge in this local test fixture
        return Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(value)));
#pragma warning restore CA5351
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

internal sealed class DigestNonce
{
    public string Value { get; } = Guid.NewGuid().ToString("N");
}
