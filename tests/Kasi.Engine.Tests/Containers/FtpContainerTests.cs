using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Kasi.Core;
using Kasi.TestServer;

namespace Kasi.Engine.Tests.Containers;

/// <summary>
/// The FTP half of the conformance leg (KASI-ENG-002, TC-ENG-022): the engine against a real vsftpd. The in-process FTP server
/// proves the engine's behaviour in detail; these tests prove the same transfers against the daemon people actually run. Passive
/// FTP, explicit FTPS with a pinned certificate, <c>REST</c> resume and a bad login.
/// </summary>
[Collection(ConformanceGroup.Name)]
[Trait("Category", "Integration")]
[Trait("Category", "ContainerIntegration")]
[Trait("Requirement", "KASI-ENG-002")]
[Trait("TC", "TC-ENG-022")]
public sealed class FtpContainerTests(ConformanceImage image) : IAsyncLifetime
{
    private const string Seed = "vsftpd-conformance";
    private const int Size = 16 * 1024 * 1024;
    private const int ControlPort = 21210;
    private const int FirstPassivePort = 21211;
    private const int LastPassivePort = 21220;
    private const string User = "kasi";
    private const string Password = "ftp-password";

    private IContainer? _vsftpd;
    private X509Certificate2? _certificate;

    public async ValueTask InitializeAsync()
    {
        if (image.Unavailable is not null)
        {
            return;
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificatePem = _certificate.ExportCertificatePem();
        var keyPem = rsa.ExportPkcs8PrivateKeyPem();

        var config = string.Join('\n',
            "listen=YES",
            "listen_ipv6=NO",
            "background=NO",
            $"listen_port={ControlPort}",
            "anonymous_enable=NO",
            "local_enable=YES",
            "write_enable=NO",
            "chroot_local_user=YES",
            "allow_writeable_chroot=YES",
            "seccomp_sandbox=NO",
            "secure_chroot_dir=/var/run/vsftpd/empty",
            "pam_service_name=vsftpd",
            "pasv_enable=YES",
            $"pasv_min_port={FirstPassivePort}",
            $"pasv_max_port={LastPassivePort}",
            "pasv_address=127.0.0.1",
            "ssl_enable=YES",
            "force_local_logins_ssl=NO",
            "force_local_data_ssl=NO",
            "require_ssl_reuse=NO", // .NET cannot reuse the control connection's TLS session on the data connection
            "rsa_cert_file=/etc/ssl/ftp-cert.pem",
            "rsa_private_key_file=/etc/ssl/ftp-key.pem",
            string.Empty);

        var builder = ConformanceImage.Role(
                $"useradd -m {User} && echo '{User}:{Password}' | chpasswd && mkdir -p /var/run/vsftpd/empty && " +
                $"cp /srv/payload.bin /home/{User}/payload.bin && chown {User} /home/{User}/payload.bin && exec vsftpd /etc/vsftpd.conf")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(config), "/etc/vsftpd.conf")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(certificatePem), "/etc/ssl/ftp-cert.pem")
            .WithResourceMapping(System.Text.Encoding.ASCII.GetBytes(keyPem), "/etc/ssl/ftp-key.pem")
            .WithResourceMapping(GeneratedContent.Bytes(Seed, Size), "/srv/payload.bin")
            // vsftpd as process 1 of a container dies (exit 139) when a .NET client opens a second FTPS session; with an init process in front it
            // is an ordinary child and the transfers work, so the daemon never runs as PID 1
            .WithCreateParameterModifier(parameters => parameters.HostConfig!.Init = true)
            .WithPortBinding(ControlPort, ControlPort);
        for (var port = FirstPassivePort; port <= LastPassivePort; port++)
        {
            builder = builder.WithPortBinding(port, port);
        }

        _vsftpd = await ConformanceImage.StartAsync(builder, ControlPort, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_vsftpd is not null)
        {
            await _vsftpd.DisposeAsync();
        }

        _certificate?.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Passive_FTP_and_explicit_FTPS_download_and_verify_against_a_real_vsftpd(bool ftps)
    {
        image.SkipWithoutDocker();
        var ct = TestContext.Current.CancellationToken;
        var folder = MakeFolder();
        try
        {
            var runner = ftps ? new FtpTransferRunner(new X509Certificate2Collection(_certificate!)) : new FtpTransferRunner();
            var outcome = await runner.DownloadAsync(Url(ftps), folder, Options(), ct);

            var completed = outcome.Should().BeOfType<TransferCompleted>(await WhyAsync(outcome)).Subject;
            completed.TotalBytes.Should().Be(Size);
            completed.Resumable.Should().BeTrue();
            (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(Seed, Size));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_stopped_transfer_resumes_with_REST_at_the_flushed_cursor_against_a_real_vsftpd()
    {
        image.SkipWithoutDocker();
        var ct = TestContext.Current.CancellationToken;
        var folder = MakeFolder();
        try
        {
            using var paused = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stopAt = new BandwidthLimiter(4 * 1024 * 1024);
            var firstOptions = Options() with
            {
                BandwidthLimiter = stopAt,
                SchedulerTick = scheduler =>
                {
                    if (scheduler.TotalWritten >= 2 * 1024 * 1024)
                    {
                        paused.Cancel();
                    }
                },
            };
            var runner = new FtpTransferRunner();
            var first = await runner.DownloadAsync(Url(ftps: false), folder, firstOptions, paused.Token);
            var stopped = first.Should().BeOfType<TransferFailed>().Subject;
            stopped.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
            stopped.BytesFlushed.Should().BeInRange(1, Size - 1);

            var resumed = await runner.ResumeAsync(
                Url(ftps: false), folder, stopped.ResolvedFileName!, Size, stopped.LastModified, stopped.SegmentsSnapshot, Options(), ct);

            var completed = resumed.Should().BeOfType<TransferCompleted>().Subject;
            (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(Seed, Size));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_wrong_password_is_an_authentication_failure_that_does_not_echo_the_password()
    {
        image.SkipWithoutDocker();
        var ct = TestContext.Current.CancellationToken;
        var folder = MakeFolder();
        try
        {
            var options = Options() with
            {
                Secret = new JobSecret { Credentials = new HttpCredentials { UserName = User, Password = "not-the-password" } },
                RetryPolicy = new TransferRetryPolicy { MaxAttempts = 2, BaseDelay = TimeSpan.FromMilliseconds(1) },
            };

            var outcome = await new FtpTransferRunner().DownloadAsync(Url(ftps: false), folder, options, ct);

            var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
            failed.ProbeFailure?.Kind.Should().Be(ProbeErrorKind.AuthRequired);
            failed.Detail.Should().NotContain("not-the-password");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>What went wrong, with the daemon's own output, so a failure on a CI runner can be read without a second run.</summary>
    private async Task<string> WhyAsync(TransferOutcome outcome)
    {
        if (outcome is not TransferFailed failure)
        {
            return string.Empty;
        }

        var (stdout, stderr) = await _vsftpd!.GetLogsAsync(ct: CancellationToken.None);
        return $"{failure.ErrorKind}: {failure.Detail}{Environment.NewLine}--- vsftpd output ---{Environment.NewLine}{stdout}{stderr}";
    }

    private static Uri Url(bool ftps) => new($"{(ftps ? "ftps" : "ftp")}://127.0.0.1:{ControlPort.ToString(CultureInfo.InvariantCulture)}/payload.bin");

    private static HttpTransferOptions Options() => new()
    {
        Secret = new JobSecret { Credentials = new HttpCredentials { UserName = User, Password = Password } },
        ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, GeneratedContent.Sha256Hex(Seed, Size)),
        RetryPolicy = new TransferRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) },
    };

    private static string MakeFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-vsftpd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
