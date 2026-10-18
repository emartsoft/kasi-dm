namespace Kasi.TestServer;

/// <summary>The HTTP protocols Kestrel offers on the test server (M1.7a, ADR 0001).</summary>
public enum TestServerProtocolMode
{
    Http1,
    Http2,
    Http1AndHttp2,
}
