namespace Kasi.TestServer;

/// <summary>
/// Optional behaviour of <see cref="TestServerHost"/> (M1.7a, ADR 0001). The defaults keep every existing test on plain HTTP/1.1.
/// </summary>
public sealed record TestServerOptions
{
    public TestServerTlsMode TlsMode { get; init; } = TestServerTlsMode.None;

    /// <summary>Null means HTTP/1.1 for plaintext and HTTP/1.1 and HTTP/2 when TLS is on.</summary>
    public TestServerProtocolMode? ProtocolMode { get; init; }

    /// <summary>Listen on every IPv4 interface instead of loopback, so a container can reach the server (conformance tests, M1.16b).</summary>
    public bool BindAny { get; init; }

    /// <summary>The loopback port to listen on; 0 picks a free one. A fixed port lets a job's URL survive a restart of the host (power-cut runs).</summary>
    public int Port { get; init; }

    internal TestServerProtocolMode EffectiveProtocolMode =>
        ProtocolMode ?? (TlsMode == TestServerTlsMode.None ? TestServerProtocolMode.Http1 : TestServerProtocolMode.Http1AndHttp2);
}
