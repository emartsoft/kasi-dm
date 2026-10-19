using System.Security.Cryptography.X509Certificates;
using Kasi.Core;
using Kasi.TestServer;
using Kasi.TestServer.Proxy;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-011")]
[Trait("TC", "TC-ENG-019")]
public sealed class ProxyIntegrationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Transfer_uses_the_configured_proxy_and_verifies_the_download(bool tls, bool socks5, bool tlsProxy)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = tls ? TestServerTlsMode.Valid : TestServerTlsMode.None },
            cancellationToken: ct);
        await using var proxy = ProxyTestServer.Start(host.BaseAddress.Port, socks5, tlsProxy: tlsProxy);
        var folder = MakeFolder();
        try
        {
            const string seed = "proxy-transfer";
            const int size = 256 * 1024;
            var roots = new X509Certificate2Collection();
            if (tls)
            {
                roots.Add(host.RootCertificate!);
            }

            if (tlsProxy)
            {
                roots.Add(proxy.RootCertificate!);
            }

            var runner = new HttpTransferRunner(new HttpHostPoolOptions
            {
                CustomTrustRoots = roots.Count > 0 ? roots : null,
            });
            var outcome = await runner.DownloadAsync(VirtualUrl(host, seed, size), folder,
                Options(proxy.Address, seed, size), ct);

            var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
            File.Exists(completed.FinalPath).Should().BeTrue();
            proxy.Connections.Should().BeGreaterThan(0);
            proxy.BytesRelayed.Should().BeGreaterThan(size);
            proxy.Connects.Should().Be(tls && !socks5 ? proxy.Connections : 0);
            proxy.SocksHandshakes.Should().Be(socks5 ? proxy.Connections : 0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Basic_proxy_credentials_are_challenged_and_never_in_a_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        await using var proxy = ProxyTestServer.Start(host.BaseAddress.Port, user: "proxy-user", password: "proxy-password");
        var folder = MakeFolder();
        try
        {
            const string seed = "proxy-auth";
            const int size = 64 * 1024;
            var good = Options(proxy.Address, seed, size) with
            {
                Secret = new JobSecret
                {
                    ProxyCredentials = new HttpCredentials { UserName = "proxy-user", Password = "proxy-password" },
                },
            };
            var completed = await new HttpTransferRunner().DownloadAsync(VirtualUrl(host, seed, size), folder, good, ct);
            completed.Should().BeOfType<TransferCompleted>();

            var bad = good with
            {
                OverrideFileName = "bad.bin",
                Secret = new JobSecret
                {
                    ProxyCredentials = new HttpCredentials { UserName = "proxy-user", Password = "wrong-password" },
                },
            };
            var attempts = proxy.AuthFailures;
            var failure = await new HttpTransferRunner().DownloadAsync(VirtualUrl(host, seed, size), folder, bad, ct);
            var failed = failure.Should().BeOfType<TransferFailed>().Subject;
            failed.Detail.Should().NotContain("wrong-password");
            failed.Detail.Should().NotContain("proxy-user");
            (proxy.AuthFailures - attempts).Should().BeLessThanOrEqualTo(2);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Socks5_username_password_authentication_works()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        await using var proxy = ProxyTestServer.Start(host.BaseAddress.Port, socks5: true, user: "socks-user", password: "socks-password");
        var folder = MakeFolder();
        try
        {
            const string seed = "socks-auth";
            const int size = 64 * 1024;
            var options = Options(proxy.Address, seed, size) with
            {
                Secret = new JobSecret
                {
                    ProxyCredentials = new HttpCredentials { UserName = "socks-user", Password = "socks-password" },
                },
            };
            var outcome = await new HttpTransferRunner().DownloadAsync(VirtualUrl(host, seed, size), folder, options, ct);
            outcome.Should().BeOfType<TransferCompleted>();
            proxy.SocksHandshakes.Should().BeGreaterThan(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Connect_proxy_rejects_bad_credentials_without_retrying()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(
            new TestServerOptions { TlsMode = TestServerTlsMode.Valid }, cancellationToken: ct);
        await using var proxy = ProxyTestServer.Start(host.BaseAddress.Port, user: "connect-user", password: "connect-password");
        var folder = MakeFolder();
        try
        {
            const string seed = "connect-auth";
            const int size = 64 * 1024;
            var options = Options(proxy.Address, seed, size) with
            {
                Secret = new JobSecret
                {
                    ProxyCredentials = new HttpCredentials { UserName = "connect-user", Password = "wrong-connect-password" },
                },
                RetryPolicy = new TransferRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) },
            };
            var runner = new HttpTransferRunner(new HttpHostPoolOptions
            {
                CustomTrustRoots = new X509Certificate2Collection(host.RootCertificate!),
            });
            var outcome = await runner.DownloadAsync(VirtualUrl(host, seed, size), folder, options, ct);

            var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
            failed.Detail.Should().NotContain("wrong-connect-password");
            proxy.AuthFailures.Should().BeLessThanOrEqualTo(2);
            proxy.Connects.Should().Be(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Direct_connection_to_proxy_only_host_fails_and_a_bypass_host_connects_directly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        await using var proxy = ProxyTestServer.Start(host.BaseAddress.Port);
        var folder = MakeFolder();
        try
        {
            const string seed = "proxy-bypass";
            const int size = 64 * 1024;
            var direct = Options(proxy.Address, seed, size) with
            {
                Proxy = new ProxySettings { Mode = ProxyMode.None },
                RetryPolicy = new TransferRetryPolicy { MaxAttempts = 1 },
            };
            var outcome = await new HttpTransferRunner(new HttpHostPoolOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(2),
            }).DownloadAsync(VirtualUrl(host, seed, size), folder, direct, ct);
            outcome.Should().BeOfType<TransferFailed>();
            proxy.Connections.Should().Be(0);

            var bypass = Options(proxy.Address, seed, size) with
            {
                Proxy = new ProxySettings
                {
                    Mode = ProxyMode.Custom,
                    Uri = proxy.Address,
                    BypassHosts = ["127.0.0.1"],
                },
            };
            var completed = await new HttpTransferRunner().DownloadAsync(
                new Uri(host.BaseAddress, $"/files/{seed}/{size}"), folder, bypass, ct);
            completed.Should().BeOfType<TransferCompleted>();
            proxy.Connections.Should().Be(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Invalid_proxy_endpoints_are_rejected_before_network_access()
    {
        var invalid = new[]
        {
            new ProxySettings { Mode = ProxyMode.Custom, Uri = new Uri("http://user:pass@localhost:8080") },
            new ProxySettings { Mode = ProxyMode.Custom, Uri = new Uri("ftp://localhost:8080") },
            new ProxySettings { Mode = ProxyMode.Custom, Uri = new Uri("http://localhost:8080/path") },
        };

        foreach (var settings in invalid)
        {
            var options = new HttpTransferOptions { Proxy = settings };
            var action = options.Validate;
            action.Should().Throw<ArgumentException>();
        }
    }

    private static Uri VirtualUrl(TestServerHost host, string seed, int size) =>
        new UriBuilder(host.BaseAddress) { Host = "kasi-origin.test", Path = $"/files/{seed}/{size}" }.Uri;

    private static HttpTransferOptions Options(Uri proxy, string seed, int size) => new()
    {
        Proxy = new ProxySettings { Mode = ProxyMode.Custom, Uri = proxy },
        ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, GeneratedContent.Sha256Hex(seed, size)),
        SchedulerOptions = new SegmentSchedulerOptions { InitialConnections = 2, MaxConnections = 2 },
        RetryPolicy = new TransferRetryPolicy { MaxAttempts = 1 },
    };

    private static string MakeFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
