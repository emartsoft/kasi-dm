using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Kasi.FaultInjection.Toxiproxy;

/// <summary>One toxic on a Toxiproxy proxy; the factories cover the ones the soak uses.</summary>
public sealed record Toxic(string Name, string Type, IReadOnlyDictionary<string, long> Attributes, string Stream = "downstream", double Toxicity = 1.0)
{
    /// <summary>Delays every chunk by <paramref name="milliseconds"/> ± <paramref name="jitterMilliseconds"/>.</summary>
    public static Toxic Latency(string name, long milliseconds, long jitterMilliseconds) =>
        new(name, "latency", new Dictionary<string, long> { ["latency"] = milliseconds, ["jitter"] = jitterMilliseconds });

    /// <summary>Limits the throughput to <paramref name="kibPerSecond"/> KiB/s.</summary>
    public static Toxic Bandwidth(string name, long kibPerSecond) =>
        new(name, "bandwidth", new Dictionary<string, long> { ["rate"] = kibPerSecond });

    /// <summary>Holds all data back: with 0 the connection stalls until the toxic is removed (a partition), otherwise it closes after <paramref name="milliseconds"/>.</summary>
    public static Toxic Timeout(string name, long milliseconds) =>
        new(name, "timeout", new Dictionary<string, long> { ["timeout"] = milliseconds });

    /// <summary>Resets the TCP connection after <paramref name="milliseconds"/>.</summary>
    public static Toxic ResetPeer(string name, long milliseconds) =>
        new(name, "reset_peer", new Dictionary<string, long> { ["timeout"] = milliseconds });
}

/// <summary>
/// A client for Toxiproxy's REST API (the daemon listens on 8474 by default). It needs no package: proxies and toxics are small
/// JSON bodies. Messages never include the URL, so nothing sensitive reaches a report.
/// </summary>
public sealed class ToxiproxyClient(HttpClient http)
{
    /// <summary>Creates a proxy that listens on <paramref name="listen"/> (port 0 picks a free one) and forwards to <paramref name="upstream"/>; returns the address it really listens on.</summary>
    public async Task<string> CreateProxyAsync(string name, string listen, string upstream, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(
            "proxies",
            new JsonObject { ["name"] = name, ["listen"] = listen, ["upstream"] = upstream, ["enabled"] = true },
            cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, $"create proxy '{name}'", cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false);
        return (string?)body?["listen"] ?? throw new InvalidOperationException("Toxiproxy did not return the listen address.");
    }

    public async Task DeleteProxyAsync(string name, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"proxies/{Uri.EscapeDataString(name)}", cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, $"delete proxy '{name}'", cancellationToken).ConfigureAwait(false);
    }

    public async Task AddToxicAsync(string proxy, Toxic toxic, CancellationToken cancellationToken = default)
    {
        var attributes = new JsonObject();
        foreach (var (key, value) in toxic.Attributes)
        {
            attributes[key] = value;
        }

        using var response = await http.PostAsJsonAsync(
            $"proxies/{Uri.EscapeDataString(proxy)}/toxics",
            new JsonObject
            {
                ["name"] = toxic.Name,
                ["type"] = toxic.Type,
                ["stream"] = toxic.Stream,
                ["toxicity"] = toxic.Toxicity,
                ["attributes"] = attributes,
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, $"add toxic '{toxic.Name}'", cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveToxicAsync(string proxy, string toxic, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync(
            $"proxies/{Uri.EscapeDataString(proxy)}/toxics/{Uri.EscapeDataString(toxic)}",
            cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, $"remove toxic '{toxic}'", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes every toxic and enables every proxy (it does not delete proxies).</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("reset", content: null, cancellationToken).ConfigureAwait(false);
        await EnsureAsync(response, "reset", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The port part of a listen address such as <c>127.0.0.1:34567</c>.</summary>
    public static int PortOf(string listen) =>
        int.Parse(listen[(listen.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var detail = text.Length <= 200 ? text : text[..200];
        throw new InvalidOperationException($"Toxiproxy refused to {what} ({(int)response.StatusCode}): {detail}");
    }
}
