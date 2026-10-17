namespace Kasi.TestServer.Endpoints;

/// <summary><c>/disposition/{case}</c>: a small body with an edge-case <c>Content-Disposition</c> (raw, unsanitised).</summary>
public static class DispositionEndpoints
{
    public static IReadOnlyDictionary<string, string> Cases { get; } = new Dictionary<string, string>
    {
        ["rfc5987"] = "attachment; filename*=UTF-8''%E2%82%AC%20rates.txt",
        ["rfc5987-with-fallback"] = "attachment; filename=\"rates.txt\"; filename*=UTF-8''%E2%82%AC%20rates.txt",
        ["traversal"] = "attachment; filename=\"../../evil.txt\"",
        ["traversal-backslash"] = "attachment; filename=\"..\\..\\evil.txt\"",
        ["traversal-encoded"] = "attachment; filename*=UTF-8''..%2F..%2Fevil.txt",
        ["absolute-path"] = "attachment; filename=\"C:\\Windows\\System32\\drivers\\etc\\hosts\"",
        ["reserved"] = "attachment; filename=\"CON.txt\"",
        ["reserved-bare"] = "attachment; filename=\"NUL\"",
        ["quoted-escapes"] = "attachment; filename=\"a \\\"quoted\\\" name.txt\"",
        ["semicolon"] = "attachment; filename=\"a;b.txt\"",
        ["trailing-dots-spaces"] = "attachment; filename=\"report.txt. . \"",
        ["invalid-chars"] = "attachment; filename=\"a<b>c:d|e?f*.txt\"",
        ["long"] = "attachment; filename=\"" + new string('a', 300) + ".bin\"",
        ["empty"] = "attachment; filename=\"\"",
        ["no-filename"] = "attachment",
        ["inline"] = "inline; filename=\"inline.pdf\"",
    };

    internal static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/disposition/{name}", async (HttpContext context, string name) =>
        {
            if (!Cases.TryGetValue(name, out var value))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "application/octet-stream";
            context.Response.Headers.ContentDisposition = value;
            const int size = 16;
            context.Response.ContentLength = size;
            await FileEndpoints.WriteBodyAsync(context.Response.Body, "disposition-" + name, size, context.RequestAborted);
        });
}
