using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Kasi.Engine;

/// <summary>
/// Owns the HTTP invokers for one transfer (KASI-ENG-001, KASI-ENG-006, KASI-NFR-011; ADR 0006). A lane is keyed by
/// <c>(origin, workerId)</c> and holds one <see cref="SocketsHttpHandler"/> with <c>MaxConnectionsPerServer = 1</c>, so each
/// worker reuses its own keep-alive connection and HTTP/2 gets one connection per worker instead of multiplexing every segment
/// on the connection the probe happened to open. Disposing the pool disposes every lane.
/// </summary>
public sealed class HttpHostPool : IDisposable
{
    private readonly HttpHostPoolOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<LaneKey, Lane> _lanes = [];
    private bool _disposed;

    public HttpHostPool(HttpHostPoolOptions? options = null)
    {
        _options = options ?? new HttpHostPoolOptions();
        _options.Validate();
    }

    /// <summary>
    /// Returns the invoker for <paramref name="url"/>'s origin on <paramref name="workerId"/>'s lane, creating it on first use.
    /// The returned invoker is owned by the pool; do not dispose it.
    /// </summary>
    public HttpMessageInvoker GetInvoker(Uri url, int workerId)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentOutOfRangeException.ThrowIfNegative(workerId);
        if (!IsSupported(url))
        {
            throw new ArgumentException("Only absolute HTTP and HTTPS URLs have a host lane.", nameof(url));
        }

        var key = new LaneKey(Origin(url), workerId);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lanes.TryGetValue(key, out var existing))
            {
                return existing.Invoker;
            }

#pragma warning disable CA2000 // the invoker owns the handler and the pool owns the invoker
            var handler = CreateHandler();
            var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
#pragma warning restore CA2000
            _lanes.Add(key, new Lane(invoker, handler));
            return invoker;
        }
    }

    /// <summary>Number of created lanes; for diagnostics and tests.</summary>
    internal int LaneCount
    {
        get
        {
            lock (_gate)
            {
                return _lanes.Count;
            }
        }
    }

    /// <summary>Test-only access to the handler settings; production code must go through <see cref="GetInvoker"/>.</summary>
    internal SocketsHttpHandler GetHandlerForTests(Uri url, int workerId)
    {
#pragma warning disable CA2000 // GetInvoker transfers ownership to the pool, which disposes it
        _ = GetInvoker(url, workerId);
#pragma warning restore CA2000
        lock (_gate)
        {
            return _lanes[new LaneKey(Origin(url), workerId)].Handler;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var lane in _lanes.Values)
            {
                lane.Invoker.Dispose();
            }

            _lanes.Clear();
        }
    }

    private SocketsHttpHandler CreateHandler()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = _options.ConnectTimeout,
            PooledConnectionIdleTimeout = _options.PooledConnectionIdleTimeout,
            PooledConnectionLifetime = _options.PooledConnectionLifetime,
            KeepAlivePingDelay = _options.KeepAlivePingDelay,
            KeepAlivePingTimeout = _options.KeepAlivePingTimeout,
            EnableMultipleHttp2Connections = _options.EnableMultipleHttp2Connections,
            MaxConnectionsPerServer = 1,
            SslOptions = new SslClientAuthenticationOptions(),
        };

        if (_options.CustomTrustRoots is { Count: > 0 } roots)
        {
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
            };
            policy.CustomTrustStore.AddRange(roots);
            handler.SslOptions.CertificateChainPolicy = policy;
        }

        return handler;
    }

    private static string Origin(Uri url) => url.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

    private static bool IsSupported(Uri url) =>
        url.IsAbsoluteUri
        && (string.Equals(url.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private readonly record struct LaneKey(string Origin, int WorkerId);

    private sealed record Lane(HttpMessageInvoker Invoker, SocketsHttpHandler Handler);
}
