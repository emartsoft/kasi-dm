using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kasi.TestServer.Chaos;

namespace Kasi.TestServer.Tests;

[Trait("Category", "Integration")]
[Trait("TC", "TC-ENG-023")]
[Trait("Requirement", "KASI-NFR-011")]
[Trait("Requirement", "KASI-ENG-001")]
public sealed class TlsAndHttp2ServerTests
{
    [Fact]
    public async Task Valid_tls_serves_HTTP1_and_HTTP2_with_one_handshake_per_connection_and_a_chain_that_validates_against_the_root()
    {
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = TestServerTlsMode.Valid },
            cancellationToken: TestClient.Ct);

        using var http1 = CreateClient(host, HttpVersion.Version11);
        using var http2 = CreateClient(host, HttpVersion.Version20);

        using var first = await http1.GetAsync("/files/tls-seed/65536", TestClient.Ct);
        var firstBody = await first.Content.ReadAsByteArrayAsync(TestClient.Ct);
        using var second = await http2.GetAsync("/files/tls-seed/65536", TestClient.Ct);
        var secondBody = await second.Content.ReadAsByteArrayAsync(TestClient.Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        Convert.ToHexStringLower(SHA256.HashData(firstBody)).Should().Be(GeneratedContent.Sha256Hex("tls-seed", 65536));
        Convert.ToHexStringLower(SHA256.HashData(secondBody)).Should().Be(GeneratedContent.Sha256Hex("tls-seed", 65536));

        host.Metrics.Http1RequestsServed.Should().Be(1);
        host.Metrics.Http2RequestsServed.Should().Be(1);
        host.Metrics.TlsHandshakesCompleted.Should().Be(2);
        host.Metrics.TotalConnectionsAccepted.Should().Be(2);
        host.Metrics.PeakConcurrentConnections.Should().BeGreaterThanOrEqualTo(1);

        using var leaf = await ReadServerCertificateAsync(host, "127.0.0.1");
        leaf.Should().NotBeNull();
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.CustomTrustStore.Add(host.RootCertificate!);
        chain.Build(leaf!).Should().BeTrue("the valid leaf must chain to the exposed root");
    }

    [Theory]
    [InlineData(TestServerTlsMode.Expired, SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(TestServerTlsMode.SelfSigned, SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(TestServerTlsMode.WrongHost, SslPolicyErrors.RemoteCertificateNameMismatch)]
    public async Task Bad_certificate_modes_fail_standard_validation_and_are_not_counted_as_completed_handshakes(
        TestServerTlsMode mode,
        SslPolicyErrors expected)
    {
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = mode },
            cancellationToken: TestClient.Ct);

        var errors = await AuthenticateAndCaptureErrorsAsync(host, "127.0.0.1");
        errors.Should().NotBe(SslPolicyErrors.None);
        (errors & expected).Should().Be(expected, "the {0} mode should report {1}", mode, expected);

        host.Metrics.TlsHandshakesCompleted.Should().Be(0);
        host.Metrics.TotalConnectionsAccepted.Should().Be(1);

        using var client = CreateClient(host, HttpVersion.Version11);
        var act = async () => await client.GetAsync("/files/bad/64", TestClient.Ct);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task A_completed_request_read_to_EOF_is_not_counted_as_aborted_but_a_client_cancellation_mid_stream_is()
    {
        await using var host = await TestServerHost.StartAsync(cancellationToken: TestClient.Ct);
        using var client = CreateClient(host, HttpVersion.Version11);

        var body = await client.GetByteArrayAsync("/files/abort-clean/1024", TestClient.Ct);
        body.Should().HaveCount(1024);
        host.Metrics.AbortedRequests.Should().Be(0);

        var chaosId = host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 128 * 1024 });
        using var slowClient = TestClient.Create(host, chaosId);
        using var cts = new CancellationTokenSource();
        using var response = await slowClient.GetAsync("/files/abort-mid/8388608", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using (var stream = await response.Content.ReadAsStreamAsync(TestClient.Ct))
        {
            var buffer = new byte[1024];
            await stream.ReadExactlyAsync(buffer, TestClient.Ct);
        }

        await cts.CancelAsync();
        response.Dispose();

        await EventuallyAsync(() => host.Metrics.AbortedRequests == 1, "the server should observe the aborted request");
    }

    [Fact]
    public async Task OtherConnectionsBytesPerSecond_throttles_every_connection_except_the_indexed_one()
    {
        await using var host = await TestServerHost.StartAsync(cancellationToken: TestClient.Ct);
        var chaosId = host.Chaos.Register(new ChaosProfile
        {
            ThrottleConnectionIndex = 0,
            ThrottleBytesPerSecond = 64 * 1024,
            OtherConnectionsBytesPerSecond = 2 * 1024 * 1024,
        });
        using var slowClient = TestClient.Create(host, chaosId);
        using var fastClient = TestClient.Create(host, chaosId);

        var slowTimer = Stopwatch.StartNew();
        using var slowResponse = await slowClient.GetAsync(
            "/files/other-connection/131072",
            HttpCompletionOption.ResponseHeadersRead,
            TestClient.Ct);

        var fastTimer = Stopwatch.StartNew();
        using var fastResponse = await fastClient.GetAsync("/files/other-connection/131072", TestClient.Ct);
        var fastBody = await fastResponse.Content.ReadAsByteArrayAsync(TestClient.Ct);
        fastTimer.Stop();

        var slowBody = await slowResponse.Content.ReadAsByteArrayAsync(TestClient.Ct);
        slowTimer.Stop();

        fastBody.Should().Equal(GeneratedContent.Bytes("other-connection", 131072));
        slowBody.Should().Equal(GeneratedContent.Bytes("other-connection", 131072));
        host.Metrics.TotalConnectionsAccepted.Should().Be(2);
        slowTimer.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5), "connection 0 is throttled to 64 KiB/s");
        fastTimer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "connection 1 gets the other-connection rate");
        slowTimer.Elapsed.Should().BeGreaterThan(fastTimer.Elapsed * 2);
    }

    private static HttpClient CreateClient(TestServerHost host, Version version)
    {
#pragma warning disable CA2000 // the client owns the handler and disposes it
        var handler = new SocketsHttpHandler();
        if (host.RootCertificate is { } root)
        {
            handler.SslOptions = new SslClientAuthenticationOptions { CertificateChainPolicy = ChainPolicy(root) };
        }

        return new HttpClient(handler)
        {
            BaseAddress = host.BaseAddress,
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(30),
        };
#pragma warning restore CA2000
    }

    private static X509ChainPolicy ChainPolicy(X509Certificate2 root)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(root);
        return policy;
    }

    private static async Task<X509Certificate2> ReadServerCertificateAsync(TestServerHost host, string targetHost)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.BaseAddress.Host, host.BaseAddress.Port, TestClient.Ct);
        X509Certificate2? leaf = null;
        using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, policyErrors) =>
        {
            if (certificate is not null)
            {
                leaf = new X509Certificate2(certificate);
            }

            return policyErrors == SslPolicyErrors.None;
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = targetHost,
            CertificateChainPolicy = ChainPolicy(host.RootCertificate!),
        }, TestClient.Ct);

        leaf.Should().NotBeNull();
        return leaf!;
    }

    private static async Task<SslPolicyErrors> AuthenticateAndCaptureErrorsAsync(TestServerHost host, string targetHost)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host.BaseAddress.Host, host.BaseAddress.Port, TestClient.Ct);
        var errors = SslPolicyErrors.None;
        using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, _, _, policyErrors) =>
        {
            errors = policyErrors;
            return policyErrors == SslPolicyErrors.None;
        });

        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                CertificateChainPolicy = ChainPolicy(host.RootCertificate!),
            }, TestClient.Ct);
        }
        catch (AuthenticationException)
        {
            // Expected for the expired, self-signed and wrong-host leaves.
        }

        return errors;
    }

    private static async Task EventuallyAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, because);
            await Task.Delay(20, TestClient.Ct);
        }
    }
}
