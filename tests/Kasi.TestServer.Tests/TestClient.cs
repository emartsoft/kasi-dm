using System.Net;
using Kasi.TestServer.Chaos;

namespace Kasi.TestServer.Tests;

internal static class TestClient
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A client with its own connection pool; <paramref name="chaosId"/> is sent as <c>X-Kasi-Chaos</c>.</summary>
    public static HttpClient Create(TestServerHost host, string? chaosId = null, bool autoRedirect = true, CookieContainer? cookies = null)
    {
#pragma warning disable CA2000 // the client owns the handler and disposes it
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = autoRedirect,
            UseCookies = cookies is not null,
            CookieContainer = cookies ?? new CookieContainer(),
        };
        var client = new HttpClient(handler) { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
#pragma warning restore CA2000
        if (chaosId is not null)
        {
            client.DefaultRequestHeaders.Add(ChaosRegistry.HeaderName, chaosId);
        }

        return client;
    }

    /// <summary>Reads the body to the end and reports the bytes that arrived and whether the transfer failed part-way.</summary>
    public static async Task<(byte[] Body, Exception? Failure)> ReadBodyAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, Ct);
        using var buffer = new MemoryStream();
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(Ct);
            await body.CopyToAsync(buffer, Ct);
            return (buffer.ToArray(), null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (buffer.ToArray(), ex);
        }
    }
}
