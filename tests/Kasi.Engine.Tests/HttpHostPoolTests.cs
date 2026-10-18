using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("TC", "TC-ENG-023")]
[Trait("Requirement", "KASI-NFR-011")]
[Trait("Requirement", "KASI-ENG-001")]
[Trait("Requirement", "KASI-ENG-006")]
public sealed class HttpHostPoolTests
{
    [Fact]
    public async Task Handler_settings_are_locked_down_and_custom_roots_use_a_chain_policy_without_a_validation_callback()
    {
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = TestServerTlsMode.Valid },
            cancellationToken: TestContext.Current.CancellationToken);

        using var production = new HttpHostPool();
        var productionHandler = production.GetHandlerForTests(new Uri("https://example.com/file"), 0);
        productionHandler.AllowAutoRedirect.Should().BeFalse();
        productionHandler.AutomaticDecompression.Should().Be(DecompressionMethods.None);
        productionHandler.UseCookies.Should().BeFalse();
        productionHandler.EnableMultipleHttp2Connections.Should().BeTrue();
        productionHandler.MaxConnectionsPerServer.Should().Be(1);
        productionHandler.SslOptions.CertificateChainPolicy.Should().BeNull();
        productionHandler.SslOptions.RemoteCertificateValidationCallback.Should().BeNull();

        using var tests = new HttpHostPool(new HttpHostPoolOptions
        {
            CustomTrustRoots = new X509Certificate2Collection(host.RootCertificate!),
        });
        var testHandler = tests.GetHandlerForTests(host.BaseAddress, 0);
        testHandler.SslOptions.RemoteCertificateValidationCallback.Should().BeNull();
        var policy = testHandler.SslOptions.CertificateChainPolicy;
        policy.Should().NotBeNull();
        policy!.TrustMode.Should().Be(X509ChainTrustMode.CustomRootTrust);
        policy.RevocationMode.Should().Be(X509RevocationMode.NoCheck);
        policy.CustomTrustStore.Should().ContainSingle();
        policy.CustomTrustStore[0].Thumbprint.Should().Be(host.RootCertificate!.Thumbprint);
    }

    [Fact]
    public void Lanes_are_per_origin_and_worker_and_invalid_urls_are_rejected()
    {
        using var pool = new HttpHostPool();
        var origin = new Uri("https://example.com/a.bin");

        var first = pool.GetInvoker(origin, 0);
        var again = pool.GetInvoker(new Uri("https://example.com/b.bin"), 0);
        var secondWorker = pool.GetInvoker(origin, 1);
        var otherOrigin = pool.GetInvoker(new Uri("https://other.example.com/a.bin"), 0);

        again.Should().BeSameAs(first);
        secondWorker.Should().NotBeSameAs(first);
        otherOrigin.Should().NotBeSameAs(first);
        pool.LaneCount.Should().Be(3);

        var ftp = () => pool.GetInvoker(new Uri("ftp://example.com/a.bin"), 0);
        var negative = () => pool.GetInvoker(origin, -1);
        ftp.Should().Throw<ArgumentException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Valid_TLS_probe_uses_HTTP2_and_completes_with_the_right_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions
            {
                TlsMode = TestServerTlsMode.Valid,
                ProtocolMode = TestServerProtocolMode.Http2,
            },
            cancellationToken: ct);

        using var pool = new HttpHostPool(new HttpHostPoolOptions
        {
            CustomTrustRoots = new X509Certificate2Collection(host.RootCertificate!),
        });
        var prober = new HttpProber(pool.GetInvoker(host.BaseAddress, 0));

        var outcome = await prober.ProbeAsync(new Uri(host.BaseAddress, "/files/host-pool/65536"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        response.RangesSupported.Should().BeTrue();
        response.TotalBytes.Should().Be(65536);
        var body = await ReadAllBytesAsync(response.Body, ct);
        Convert.ToHexStringLower(SHA256.HashData(body)).Should().Be(GeneratedContent.Sha256Hex("host-pool", 65536));
        host.Metrics.Http2RequestsServed.Should().Be(1);
        host.Metrics.Http1RequestsServed.Should().Be(0);
        host.Metrics.TlsHandshakesCompleted.Should().Be(1);
    }

    [Fact]
    public async Task Two_worker_lanes_open_two_HTTP2_connections_on_the_same_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions
            {
                TlsMode = TestServerTlsMode.Valid,
                ProtocolMode = TestServerProtocolMode.Http2,
            },
            cancellationToken: ct);
        using var pool = new HttpHostPool(new HttpHostPoolOptions
        {
            CustomTrustRoots = new X509Certificate2Collection(host.RootCertificate!),
        });
        var firstProber = new HttpProber(pool.GetInvoker(host.BaseAddress, 0));
        var secondProber = new HttpProber(pool.GetInvoker(host.BaseAddress, 1));

        var outcomes = await Task.WhenAll(
            firstProber.ProbeAsync(new Uri(host.BaseAddress, "/files/host-pool-a/65536"), cancellationToken: ct),
            secondProber.ProbeAsync(new Uri(host.BaseAddress, "/files/host-pool-b/65536"), cancellationToken: ct));

        using var first = outcomes[0].Should().BeOfType<ProbeResponse>().Subject;
        using var second = outcomes[1].Should().BeOfType<ProbeResponse>().Subject;
        (await ReadAllBytesAsync(first.Body, ct)).Should().HaveCount(65536);
        (await ReadAllBytesAsync(second.Body, ct)).Should().HaveCount(65536);

        pool.LaneCount.Should().Be(2);
        host.Metrics.TotalConnectionsAccepted.Should().BeGreaterThanOrEqualTo(2);
        host.Metrics.PeakConcurrentConnections.Should().BeGreaterThanOrEqualTo(2);
        host.Metrics.TlsHandshakesCompleted.Should().Be(2);
        host.Metrics.Http2RequestsServed.Should().Be(2);
        host.Metrics.Http1RequestsServed.Should().Be(0);
    }

    [Theory]
    [InlineData(TestServerTlsMode.Expired)]
    [InlineData(TestServerTlsMode.SelfSigned)]
    [InlineData(TestServerTlsMode.WrongHost)]
    public async Task Bad_certificates_fail_the_probe_with_CertificateError(TestServerTlsMode mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = mode },
            cancellationToken: ct);
        using var pool = new HttpHostPool(new HttpHostPoolOptions
        {
            CustomTrustRoots = new X509Certificate2Collection(host.RootCertificate!),
        });
        var prober = new HttpProber(pool.GetInvoker(host.BaseAddress, 0));

        var outcome = await prober.ProbeAsync(new Uri(host.BaseAddress, "/files/bad-cert/65536"), cancellationToken: ct);

        var failure = outcome.Should().BeOfType<ProbeFailure>().Subject;
        failure.Kind.Should().Be(ProbeErrorKind.CertificateError);
        failure.StatusCode.Should().BeNull();
        host.Metrics.TlsHandshakesCompleted.Should().Be(0);
    }

    [Fact]
    public void No_public_member_of_the_pool_or_options_offers_a_validation_bypass()
    {
        var forbidden = new[] { "callback", "bypass", "validation", "insecure", "dangerous", "disable", "trustroot" };

        foreach (var type in new[] { typeof(HttpHostPool), typeof(HttpHostPoolOptions) })
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is MethodInfo { IsSpecialName: true } or ConstructorInfo)
                {
                    continue;
                }

                forbidden.Any(token => member.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                    .Should().BeFalse("{0}.{1} is public and looks like a validation bypass", type.Name, member.Name);
            }
        }

        typeof(HttpHostPoolOptions).GetProperty("CustomTrustRoots", BindingFlags.Public | BindingFlags.Instance).Should().BeNull();
        typeof(HttpHostPoolOptions).GetProperty("CustomTrustRoots", BindingFlags.NonPublic | BindingFlags.Instance).Should().NotBeNull();
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}
