using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Kasi.Core;
using Kasi.TestServer;

namespace Kasi.Engine.Tests.Containers;

/// <summary>
/// The proxy half of the conformance leg (KASI-ENG-011, TC-ENG-019): the engine through a real Squid (HTTP forward proxy,
/// <c>CONNECT</c> tunnel, Basic authentication) and a real Dante (SOCKS5 with username and password). The origin is a
/// <c>Kasi.TestServer</c> on the host; the containers reach it as <c>kasi-origin.test</c> (a name in the test certificate), mapped to
/// the host gateway, and the engine never resolves that name itself: only the proxy does.
/// </summary>
[Collection(ConformanceGroup.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "ContainerIntegration")]
[Trait("Requirement", "KASI-ENG-011")]
[Trait("TC", "TC-ENG-019")]
public sealed class ProxyContainerTests(ConformanceImage image) : IAsyncLifetime
{
    private const string ProxyUser = "proxy-user";
    private const string ProxyPassword = "proxy-password";
    private const int Size = 16 * 1024 * 1024;

    private TestServerHost? _http;
    private TestServerHost? _https;
    private IContainer? _squid;
    private IContainer? _squidWithAuth;
    private IContainer? _dante;

    public async ValueTask InitializeAsync()
    {
        if (image.Unavailable is not null)
        {
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        _http = await TestServerHost.StartAsync(new TestServerOptions { BindAny = true }, cancellationToken: ct);
        _https = await TestServerHost.StartAsync(new TestServerOptions { BindAny = true, TlsMode = TestServerTlsMode.Valid }, cancellationToken: ct);

        _squid = await StartAsync(Squid(authenticated: false), 3128, ct);
        _squidWithAuth = await StartAsync(Squid(authenticated: true), 3128, ct);
        _dante = await StartAsync(Dante(), 1080, ct);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var container in new[] { _squid, _squidWithAuth, _dante })
        {
            if (container is not null)
            {
                await container.DisposeAsync();
            }
        }

        if (_http is not null)
        {
            await _http.DisposeAsync();
        }

        if (_https is not null)
        {
            await _https.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Squid_forwards_plain_HTTP_and_tunnels_HTTPS_with_CONNECT(bool tls)
    {
        image.SkipWithoutDocker();
        var ct = TestContext.Current.CancellationToken;
        var folder = MakeFolder();
        try
        {
            var origin = tls ? _https! : _http!;
            var runner = new HttpTransferRunner(new HttpHostPoolOptions
            {
                CustomTrustRoots = tls ? new X509Certificate2Collection(origin.RootCertificate!) : null,
            });

            var outcome = await runner.DownloadAsync(OriginUrl(origin, "squid"), folder, Options(ProxyOf(_squid!, 3128), "squid"), ct);

            outcome.Should().BeOfType<TransferCompleted>();
            origin.Metrics.BytesServed.Should().BeGreaterThanOrEqualTo(Size, "the bytes came from the origin through Squid");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Squid_accepts_the_right_proxy_credentials_and_rejects_the_wrong_ones_without_echoing_them()
    {
        image.SkipWithoutDocker();
        var ct = TestContext.Current.CancellationToken;
        var folder = MakeFolder();
        try
        {
            var proxy = ProxyOf(_squidWithAuth!, 3128);
            var good = Options(proxy, "squid-auth") with { Secret = Credentials(ProxyUser, ProxyPassword) };
            (await new HttpTransferRunner().DownloadAsync(OriginUrl(_http!, "squid-auth"), folder, good, ct)).Should().BeOfType<TransferCompleted>();

            var bad = Options(proxy, "squid-auth") with { OverrideFileName = "bad.bin", Secret = Credentials(ProxyUser, "wrong-password") };
            var failed = (await new HttpTransferRunner().DownloadAsync(OriginUrl(_http!, "squid-auth"), folder, bad, ct)).Should().BeOfType<TransferFailed>().Subject;
            failed.Detail.Should().NotContain("wrong-password").And.NotContain(ProxyUser);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Dante_relays_a_download_with_SOCKS5_username_and_password_authentication()
    {
        image.SkipWithoutDocker();
        var ct = TestContext.Current.CancellationToken;
        var folder = MakeFolder();
        try
        {
            var proxy = new Uri($"socks5://127.0.0.1:{_dante!.GetMappedPublicPort(1080).ToString(CultureInfo.InvariantCulture)}");
            var good = Options(proxy, "dante") with { Secret = Credentials(ProxyUser, ProxyPassword) };
            (await new HttpTransferRunner().DownloadAsync(OriginUrl(_http!, "dante"), folder, good, ct)).Should().BeOfType<TransferCompleted>();

            var bad = Options(proxy, "dante") with { OverrideFileName = "bad.bin", Secret = Credentials(ProxyUser, "wrong-password") };
            var failed = (await new HttpTransferRunner().DownloadAsync(OriginUrl(_http!, "dante"), folder, bad, ct)).Should().BeOfType<TransferFailed>().Subject;
            failed.Detail.Should().NotContain("wrong-password");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static ContainerBuilder Squid(bool authenticated)
    {
        var config = string.Join('\n',
            "http_port 3128",
            "pid_filename none",
            "coredump_dir /tmp",
            "cache deny all",
            authenticated ? "auth_param basic program /usr/lib/squid/basic_ncsa_auth /etc/squid/passwd" : string.Empty,
            authenticated ? "auth_param basic realm kasi-conformance" : string.Empty,
            authenticated ? "acl authenticated proxy_auth REQUIRED" : string.Empty,
            authenticated ? "http_access allow authenticated" : "http_access allow all",
            "http_access deny all",
            string.Empty);
        return ConformanceImage.Role(
                $"echo \"{ProxyUser}:$(openssl passwd -apr1 {ProxyPassword})\" > /etc/squid/passwd && chmod 644 /etc/squid/passwd && exec squid -N -f /etc/squid/squid.conf")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(config), "/etc/squid/squid.conf")
            .WithExtraHost("kasi-origin.test", "host-gateway")
            .WithPortBinding(3128, true);
    }

    private static ContainerBuilder Dante()
    {
        var config = string.Join('\n',
            "logoutput: stderr",
            "internal: 0.0.0.0 port = 1080",
            "external: eth0",
            "socksmethod: username",
            "user.privileged: root",
            "user.unprivileged: nobody",
            "client pass { from: 0.0.0.0/0 to: 0.0.0.0/0 }",
            "socks pass { from: 0.0.0.0/0 to: 0.0.0.0/0 }",
            string.Empty);
        return ConformanceImage.Role($"useradd -M -s /usr/sbin/nologin {ProxyUser} && echo '{ProxyUser}:{ProxyPassword}' | chpasswd && exec danted -f /etc/danted.conf")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(config), "/etc/danted.conf")
            .WithExtraHost("kasi-origin.test", "host-gateway")
            .WithPortBinding(1080, true);
    }

    private static async Task<IContainer> StartAsync(ContainerBuilder builder, int port, CancellationToken cancellationToken)
    {
        var container = builder.WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(port)).Build();
        await container.StartAsync(cancellationToken);
        return container;
    }

    private static Uri ProxyOf(IContainer container, int port) =>
        new($"http://127.0.0.1:{container.GetMappedPublicPort(port).ToString(CultureInfo.InvariantCulture)}");

    private static Uri OriginUrl(TestServerHost origin, string seed) =>
        new UriBuilder(origin.BaseAddress) { Host = "kasi-origin.test", Path = $"/files/{seed}/{Size.ToString(CultureInfo.InvariantCulture)}" }.Uri;

    private static JobSecret Credentials(string user, string password) =>
        new() { ProxyCredentials = new HttpCredentials { UserName = user, Password = password } };

    private static HttpTransferOptions Options(Uri proxy, string seed) => new()
    {
        Proxy = new ProxySettings { Mode = ProxyMode.Custom, Uri = proxy },
        ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, GeneratedContent.Sha256Hex(seed, Size)),
        SchedulerOptions = new SegmentSchedulerOptions { InitialConnections = 2, MaxConnections = 2 },
        RetryPolicy = new TransferRetryPolicy { MaxAttempts = 1 },
    };

    private static string MakeFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-proxy-containers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
