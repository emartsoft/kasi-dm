namespace Kasi.TestServer;

/// <summary>
/// Counters a test asserts against (M1.7a, ADR 0001): accepted and concurrent connections, completed TLS handshakes, aborted
/// requests and the HTTP protocol each request used. Every member is safe to read while the server is running.
/// </summary>
public sealed class TestServerMetrics
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _tlsConnections = new(StringComparer.Ordinal);
    private int _totalConnections;
    private int _openConnections;
    private int _peakConcurrentConnections;
    private int _tlsHandshakes;
    private int _abortedRequests;
    private int _http1Requests;
    private int _http2Requests;
    private int _requestsWithAuthorization;
    private int _requestsWithCookie;
    private int _authChallenges;
    private int _signedLinkRejects;
    private int _requestsServed;
    private long _bytesServed;

    public int TotalConnectionsAccepted => Volatile.Read(ref _totalConnections);

    public int OpenConnections => Volatile.Read(ref _openConnections);

    public int PeakConcurrentConnections => Volatile.Read(ref _peakConcurrentConnections);

    /// <summary>
    /// A TLS connection that reached the request pipeline, counted once per connection. A certificate the client rejects
    /// never sends a request, so it is not counted (TC-ENG-023).
    /// </summary>
    public int TlsHandshakesCompleted => Volatile.Read(ref _tlsHandshakes);

    /// <summary>Requests whose <c>RequestAborted</c> fired for a reason other than an injected chaos reset.</summary>
    public int AbortedRequests => Volatile.Read(ref _abortedRequests);

    public int Http1RequestsServed => Volatile.Read(ref _http1Requests);

    public int Http2RequestsServed => Volatile.Read(ref _http2Requests);

    public int RequestsWithAuthorization => Volatile.Read(ref _requestsWithAuthorization);

    public int RequestsWithCookie => Volatile.Read(ref _requestsWithCookie);

    public int AuthChallengesSent => Volatile.Read(ref _authChallenges);

    public int SignedLinkRejects => Volatile.Read(ref _signedLinkRejects);

    public int RequestsServed => Volatile.Read(ref _requestsServed);

    /// <summary>Generated file bytes read for response bodies; range requests count only the requested slice.</summary>
    public long BytesServed => Volatile.Read(ref _bytesServed);

    /// <summary>
    /// Starts a new measurement. Counters are zeroed, but connections that are still open (keep-alive) stay counted as open
    /// and the peak restarts from them, and a TLS connection already counted is not counted again when it is reused. Call it
    /// while the server is idle.
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalConnections, 0);
        Interlocked.Exchange(ref _peakConcurrentConnections, Volatile.Read(ref _openConnections));
        Interlocked.Exchange(ref _tlsHandshakes, 0);
        Interlocked.Exchange(ref _abortedRequests, 0);
        Interlocked.Exchange(ref _http1Requests, 0);
        Interlocked.Exchange(ref _http2Requests, 0);
        Interlocked.Exchange(ref _requestsWithAuthorization, 0);
        Interlocked.Exchange(ref _requestsWithCookie, 0);
        Interlocked.Exchange(ref _authChallenges, 0);
        Interlocked.Exchange(ref _signedLinkRejects, 0);
        Interlocked.Exchange(ref _requestsServed, 0);
        Interlocked.Exchange(ref _bytesServed, 0);
    }

    internal void ConnectionAccepted()
    {
        Interlocked.Increment(ref _totalConnections);
        var open = Interlocked.Increment(ref _openConnections);
        var peak = Volatile.Read(ref _peakConcurrentConnections);
        while (open > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peakConcurrentConnections, open, peak);
            if (seen == peak)
            {
                break;
            }

            peak = seen;
        }
    }

    internal void ConnectionClosed(string connectionId)
    {
        _tlsConnections.TryRemove(connectionId, out _);
        Interlocked.Decrement(ref _openConnections);
    }

    internal void RequestServed(HttpContext context)
    {
        Interlocked.Increment(ref _requestsServed);
        if (!string.IsNullOrEmpty(context.Request.Headers.Authorization))
        {
            Interlocked.Increment(ref _requestsWithAuthorization);
        }

        if (!string.IsNullOrEmpty(context.Request.Headers.Cookie))
        {
            Interlocked.Increment(ref _requestsWithCookie);
        }

        var protocol = context.Request.Protocol;
        if (string.Equals(protocol, "HTTP/2", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _http2Requests);
        }
        else if (string.Equals(protocol, "HTTP/1.1", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _http1Requests);
        }

        if (context.Request.IsHttps && _tlsConnections.TryAdd(context.Connection.Id, 0))
        {
            Interlocked.Increment(ref _tlsHandshakes);
        }
    }

    internal void RequestAborted() => Interlocked.Increment(ref _abortedRequests);

    internal void AuthChallengeSent() => Interlocked.Increment(ref _authChallenges);

    internal void SignedLinkRejected() => Interlocked.Increment(ref _signedLinkRejects);

    internal void FileBytesServed(int count) => Interlocked.Add(ref _bytesServed, count);
}
