using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kasi.TestServer;

/// <summary>
/// Generates the ephemeral certificates used by <see cref="TestServerTlsMode"/> (M1.7a). The root is exposed so tests can
/// configure a custom trust store; the server leaf is re-imported through PKCS#12 so Windows SChannel can use its private key.
/// </summary>
internal sealed class TestServerCertificates : IDisposable
{
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";

    private TestServerCertificates(X509Certificate2? rootCertificate, X509Certificate2? serverCertificate)
    {
        RootCertificate = rootCertificate;
        ServerCertificate = serverCertificate;
    }

    /// <summary>Null when TLS is off; otherwise the CA that signed the valid, expired and wrong-host leaves.</summary>
    public X509Certificate2? RootCertificate { get; }

    /// <summary>Null when TLS is off; otherwise the certificate Kestrel serves.</summary>
    public X509Certificate2? ServerCertificate { get; }

    public static TestServerCertificates Create(TestServerTlsMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown TLS mode.");
        }

        if (mode == TestServerTlsMode.None)
        {
            return new TestServerCertificates(rootCertificate: null, serverCertificate: null);
        }

        var now = DateTimeOffset.UtcNow;
        var root = CreateRoot(now);
        var notBefore = now.AddMinutes(-5);
        var notAfter = now.AddDays(1);

        var server = mode switch
        {
            TestServerTlsMode.Valid => CreateLeaf(root, notBefore, notAfter, "localhost", dnsNames: ["localhost"], includeLoopbackIp: true),
            TestServerTlsMode.Expired => CreateLeaf(root, now.AddDays(-2), now.AddDays(-1), "localhost", dnsNames: ["localhost"], includeLoopbackIp: true),
            TestServerTlsMode.WrongHost => CreateLeaf(root, notBefore, notAfter, "wrong.example.test", dnsNames: ["wrong.example.test"], includeLoopbackIp: false),
            TestServerTlsMode.SelfSigned => CreateSelfSignedLeaf(notBefore, notAfter, "localhost", dnsNames: ["localhost"], includeLoopbackIp: true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown TLS mode."),
        };

        return new TestServerCertificates(root, server);
    }

    public void Dispose()
    {
        ServerCertificate?.Dispose();
        RootCertificate?.Dispose();
    }

    private static X509Certificate2 CreateRoot(DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Kasi Test Root CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        return request.CreateSelfSigned(now.AddDays(-3), now.AddDays(1));
    }

    private static X509Certificate2 CreateLeaf(
        X509Certificate2 root,
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        string commonName,
        IReadOnlyList<string> dnsNames,
        bool includeLoopbackIp)
    {
        using var key = RSA.Create(2048);
        var request = NewLeafRequest(commonName, key, dnsNames, includeLoopbackIp);
        var created = request.Create(root, notBefore, notAfter, NewSerialNumber());
        var withKey = created.HasPrivateKey ? created : created.CopyWithPrivateKey(key);
        if (!ReferenceEquals(withKey, created))
        {
            created.Dispose();
        }

        return ReloadForSChannel(withKey);
    }

    private static X509Certificate2 CreateSelfSignedLeaf(
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        string commonName,
        IReadOnlyList<string> dnsNames,
        bool includeLoopbackIp)
    {
        using var key = RSA.Create(2048);
        var request = NewLeafRequest(commonName, key, dnsNames, includeLoopbackIp);
        var created = request.CreateSelfSigned(notBefore, notAfter);
        return ReloadForSChannel(created);
    }

    private static CertificateRequest NewLeafRequest(
        string commonName,
        RSA key,
        IReadOnlyList<string> dnsNames,
        bool includeLoopbackIp)
    {
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in dnsNames)
        {
            san.AddDnsName(dnsName);
        }

        if (includeLoopbackIp)
        {
            san.AddIpAddress(IPAddress.Loopback);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(ServerAuthOid) }, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        return request;
    }

    private static X509Certificate2 ReloadForSChannel(X509Certificate2 certificate)
    {
        var pfx = certificate.Export(X509ContentType.Pfx);
        certificate.Dispose();

        var reloaded = X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
        if (!reloaded.HasPrivateKey)
        {
            reloaded.Dispose();
            throw new InvalidOperationException("The generated test certificate has no private key after the PKCS#12 reload.");
        }

        return reloaded;
    }

    private static byte[] NewSerialNumber()
    {
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01);
        return serial;
    }
}
