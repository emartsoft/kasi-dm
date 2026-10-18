namespace Kasi.TestServer;

/// <summary>How <see cref="TestServerHost"/> serves TLS (M1.7a, ADR 0001).</summary>
public enum TestServerTlsMode
{
    /// <summary>Plain HTTP on the loopback address; the default, so existing tests are unchanged.</summary>
    None,

    /// <summary>A leaf signed by an ephemeral root, valid now and for a day, with <c>localhost</c> and <c>127.0.0.1</c> SANs.</summary>
    Valid,

    /// <summary>A leaf signed by the root whose validity ended yesterday.</summary>
    Expired,

    /// <summary>A leaf that signed itself, not by the exposed <see cref="TestServerHost.RootCertificate"/>.</summary>
    SelfSigned,

    /// <summary>A leaf signed by the root with a SAN for <c>wrong.example.test</c> only.</summary>
    WrongHost,
}
