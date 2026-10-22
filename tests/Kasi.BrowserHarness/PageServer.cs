using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Kasi.TestServer;

namespace Kasi.BrowserHarness;

/// <summary>
/// The pages the browser tests open and the admin API the Node side drives (<c>/_stub/*</c>). One loopback server; it never serves
/// anything but the pages below, and the downloads themselves come from <c>Kasi.TestServer</c>.
/// </summary>
internal static class PageServer
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    // excluded.test is mapped to loopback by the browser (host-resolver-rules / network.dns.localDomains), so a download from it
    // can be excluded by site while the page and the other downloads stay on 127.0.0.1.
    public const string ExcludedHost = "excluded.test";

    public static WebApplication Create(HarnessOptions options, StubKasi stub, string testServer, Action quit)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, options.AdminPort));
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/pages/home", (int? size) => Results.Content(Home(testServer, size ?? 20_000), "text/html; charset=utf-8"));
        // A slow server: the answer (a redirect to the file) comes after the delay, so downloads.onCreated fires long after the click.
        app.MapGet("/slow/{ms:int}/{seed}/{size:long}", async (int ms, string seed, long size, CancellationToken cancel) =>
        {
            await Task.Delay(Math.Min(ms, 30_000), cancel);
            return Results.Redirect($"{testServer}/files/{seed}/{size}");
        });
        app.MapGet("/_stub/sha256/{seed}/{size:long}", (string seed, long size) => Results.Text(GeneratedContent.Sha256Hex(seed, size)));
        app.MapGet("/_stub/ready", () => Results.Ok(new { stub = stub.Running }));
        app.MapGet("/_stub/received", () => Results.Json(stub.Received, _json));
        app.MapGet("/_stub/received-batches", () => Results.Json(stub.Batches, _json));
        // 40 links to files of the test server; the first 12 sit in #picked, so a test can select exactly those (TC-BRW-005)
        app.MapGet("/pages/links", () => Results.Content(Links(testServer), "text/html; charset=utf-8"));
        app.MapGet("/_stub/hellos", () => Results.Json(new { count = stub.Hellos }, _json));
        app.MapDelete("/_stub/received", () =>
        {
            stub.Clear();
            return Results.NoContent();
        });
        app.MapPost("/_stub/mode", (ModeBody body) =>
        {
            stub.SetMode(body.Mode, body.AnswerDelayMs);
            return Results.NoContent();
        });
        app.MapPost("/_stub/rules", async (RulesOptions rules) =>
        {
            await stub.SetRulesAsync(rules);
            return Results.NoContent();
        });
        app.MapPost("/_stub/stop-pipe", async () =>
        {
            await stub.StopAsync();
            return Results.NoContent();
        });
        app.MapPost("/_stub/start-pipe", () =>
        {
            if (!stub.Running)
            {
                stub.Start();
            }

            return Results.NoContent();
        });
        app.MapPost("/_stub/quit", () =>
        {
            quit();
            return Results.NoContent();
        });
        return app;
    }

    internal sealed record ModeBody(string Mode, int AnswerDelayMs);

    private static string Links(string testServer)
    {
        var page = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>Kasi links</title><body><div id=\"picked\">");
        for (var i = 0; i < 40; i++)
        {
            if (i == 12)
            {
                page.Append("</div><div id=\"rest\">");
            }

            page.Append(CultureInfo.InvariantCulture, $"<a href=\"{testServer}/files/doc{i}/1000\">doc {i}</a> ");
        }

        page.Append("</div></body>");
        return page.ToString();
    }

    private static string Home(string testServer, int size)
    {
        var port = new Uri(testServer).Port;
        var excluded = $"http://{ExcludedHost}:{port}";
        var page = new StringBuilder("<!doctype html><meta charset=\"utf-8\"><title>Kasi e2e</title><body>");
        page.Append(CultureInfo.InvariantCulture, $"<a id=\"plain\" href=\"{testServer}/files/plain/{size}\">plain</a> ");
        page.Append(CultureInfo.InvariantCulture, $"<a id=\"attr\" href=\"{testServer}/files/attr/{size}\" download>attr</a> ");
        page.Append(CultureInfo.InvariantCulture, $"<a id=\"slow\" href=\"/slow/3000/slow/{size}\">slow</a> ");
        page.Append(CultureInfo.InvariantCulture, $"<a id=\"excluded\" href=\"{excluded}/files/excluded/{size}\">excluded</a> ");
        page.Append("<a id=\"data\" download=\"data.bin\" href=\"data:application/octet-stream;base64,S2FzaQ==\">data</a> ");
        page.Append("<a id=\"blob\" href=\"#\">blob</a> ");
        // a download started by the page's own script: no link for the browser to treat specially, so a held key is the only signal
        page.Append(CultureInfo.InvariantCulture, $"<button id=\"scripted\" onclick=\"location.href='{testServer}/files/scripted/{size}'\">scripted</button>");
        page.Append(CultureInfo.InvariantCulture, $"<form method=\"post\" action=\"{testServer}/post-download/post/{size}\"><button id=\"post\">post</button></form>");
        page.Append("<script>document.getElementById('blob').addEventListener('click',e=>{e.preventDefault();");
        page.Append("const a=document.createElement('a');a.href=URL.createObjectURL(new Blob(['Kasi'],{type:'application/octet-stream'}));");
        page.Append("a.download='blob.bin';document.body.append(a);a.click();});</script></body>");
        return page.ToString();
    }
}
