using System.Security.Cryptography.X509Certificates;
using Kasi.Core;
using Kasi.TestServer;
using Kasi.TestServer.Ftp;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-002")]
[Trait("TC", "TC-ENG-022")]
public sealed class FtpTransferIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Passive_FTP_and_explicit_FTPS_download_and_verify_the_file(bool ftps)
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-passive";
        const int size = 16 * 1024 * 1024;
        await using var server = FtpTestServer.Start(seed, size, ftps);
        var folder = MakeFolder();
        try
        {
            var runner = ftps
                ? new FtpTransferRunner(new X509Certificate2Collection(server.RootCertificate!))
                : new FtpTransferRunner();
            var outcome = await runner.DownloadAsync(Url(server, seed, size), folder, Options(seed, size), ct);

            var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
            completed.TotalBytes.Should().Be(size);
            completed.Resumable.Should().BeTrue();
            (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(seed, size));
            server.BytesSent.Should().Be(size);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_dropped_data_connection_retries_with_REST_at_the_saved_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-rest";
        const int size = 4 * 1024 * 1024;
        await using var server = FtpTestServer.Start(seed, size);
        server.DropOneDataConnectionAfter(512 * 1024);
        var folder = MakeFolder();
        try
        {
            var outcome = await new FtpTransferRunner().DownloadAsync(Url(server, seed, size), folder,
                Options(seed, size), ct);

            var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
            completed.RetryCount.Should().BeGreaterThan(0);
            server.RestOffsets.Should().Contain(offset => offset >= 512 * 1024);
            server.BytesSent.Should().Be(size);
            (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(seed, size));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Paused_FTP_job_resumes_from_its_flushed_cursor()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-pause";
        const int size = 4 * 1024 * 1024;
        await using var server = FtpTestServer.Start(seed, size);
        server.DataChunkDelay = TimeSpan.FromMilliseconds(5);
        var folder = MakeFolder();
        try
        {
            using var paused = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var options = Options(seed, size) with
            {
                SchedulerTick = scheduler =>
                {
                    if (scheduler.TotalWritten >= 512 * 1024)
                    {
                        paused.Cancel();
                    }
                },
            };
            var runner = new FtpTransferRunner();
            var first = await runner.DownloadAsync(Url(server, seed, size), folder, options, paused.Token);
            var stopped = first.Should().BeOfType<TransferFailed>().Subject;
            stopped.ErrorKind.Should().Be(TransferErrorKind.Cancelled);
            stopped.BytesFlushed.Should().BeGreaterThan(0);
            for (var i = 0; i < 100 && server.ActiveDataTransfers > 0; i++)
            {
                await Task.Delay(10, ct);
            }

            server.ActiveDataTransfers.Should().Be(0);
            var sentBeforeResume = server.BytesSent;

            server.DataChunkDelay = TimeSpan.Zero;
            var resumed = await runner.ResumeAsync(Url(server, seed, size), folder, stopped.ResolvedFileName!,
                size, stopped.LastModified, stopped.SegmentsSnapshot, Options(seed, size), ct);
            var completed = resumed.Should().BeOfType<TransferCompleted>().Subject;
            server.RestOffsets.Should().Contain(stopped.BytesFlushed);
            (server.BytesSent - sentBeforeResume).Should().Be(size - stopped.BytesFlushed);
            (await File.ReadAllBytesAsync(completed.FinalPath, ct)).Should().Equal(GeneratedContent.Bytes(seed, size));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Wrong_FTP_checksum_keeps_the_part_file_and_old_destination()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-checksum";
        const int size = 64 * 1024;
        await using var server = FtpTestServer.Start(seed, size);
        var folder = MakeFolder();
        try
        {
            var final = Path.Combine(folder, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await File.WriteAllTextAsync(final, "old file", ct);
            var options = Options(seed, size) with
            {
                ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, new string('0', 64)),
                OverwriteDestination = true,
            };
            var outcome = await new FtpTransferRunner().DownloadAsync(Url(server, seed, size), folder, options, ct);
            outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.VerificationFailed);
            (await File.ReadAllTextAsync(final, ct)).Should().Be("old file");
            File.Exists(final + PartFileWriter.PartExtension).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task FTP_resume_rejects_a_changed_size_or_modification_time()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-change";
        const int size = 1024 * 1024;
        await using var server = FtpTestServer.Start(seed, size);
        var folder = MakeFolder();
        try
        {
            var url = Url(server, seed, size);
            var completed = await new FtpTransferRunner().DownloadAsync(url, folder, Options(seed, size), ct);
            var metadata = completed.Should().BeOfType<TransferCompleted>().Subject;
            server.SetSize(size + 1);
            var outcome = await new FtpTransferRunner().ResumeAsync(url, folder, metadata.FileName,
                size, metadata.LastModified, [new Segment(0, 0, size, 0)], Options(seed, size), ct);
            outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.FileChanged);

            server.SetSize(size);
            server.SetModifiedTime(new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));
            outcome = await new FtpTransferRunner().ResumeAsync(url, folder, metadata.FileName,
                size, metadata.LastModified, [new Segment(0, 0, size, 0)], Options(seed, size), ct);
            outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.FileChanged);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Missing_REST_or_MDTM_is_not_resumable(bool restSupported, bool mdtmSupported)
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-noresume";
        const int size = 64 * 1024;
        await using var server = FtpTestServer.Start(seed, size, restSupported: restSupported, mdtmSupported: mdtmSupported);
        var folder = MakeFolder();
        try
        {
            var outcome = await new FtpTransferRunner().DownloadAsync(Url(server, seed, size), folder,
                Options(seed, size), ct);
            var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
            completed.Resumable.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Bad_login_is_classified_as_authentication_and_does_not_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-auth";
        const int size = 64 * 1024;
        await using var server = FtpTestServer.Start(seed, size, user: "ftp-user", password: "ftp-password");
        var folder = MakeFolder();
        try
        {
            var options = Options(seed, size) with
            {
                Secret = new JobSecret { Credentials = new HttpCredentials { UserName = "ftp-user", Password = "wrong-password" } },
                RetryPolicy = new TransferRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) },
            };
            var outcome = await new FtpTransferRunner().DownloadAsync(Url(server, seed, size), folder, options, ct);
            var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
            failed.ProbeFailure?.Kind.Should().Be(ProbeErrorKind.AuthRequired);
            failed.Detail.Should().NotContain("wrong-password");
            server.AuthFailures.Should().Be(1);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Credentials_in_a_URL_are_rejected_without_echoing_them()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-url-secret";
        const int size = 1024;
        await using var server = FtpTestServer.Start(seed, size);
        var folder = MakeFolder();
        try
        {
            var url = new UriBuilder(Url(server, seed, size)) { UserName = "alice", Password = "secret-value" }.Uri;
            var extracted = FtpUrl.StripUserInfo(url);
            extracted.CleanUrl.UserInfo.Should().BeEmpty();
            extracted.Credentials?.Password.Should().Be("secret-value");
            var outcome = await new FtpTransferRunner().DownloadAsync(url, folder, Options(seed, size), ct);
            var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
            failed.Detail.Should().NotContain("secret-value");
            server.Connections.Should().Be(0);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task FTPS_rejects_an_untrusted_certificate_before_writing()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftps-cert";
        const int size = 64 * 1024;
        await using var server = FtpTestServer.Start(seed, size, ftps: true);
        var folder = MakeFolder();
        try
        {
            var outcome = await new FtpTransferRunner().DownloadAsync(Url(server, seed, size), folder,
                Options(seed, size), ct);
            outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.CertificateError);
            Directory.GetFiles(folder).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Requested_FTPS_never_falls_back_to_plain_FTP()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftps-required";
        const int size = 64 * 1024;
        await using var server = FtpTestServer.Start(seed, size);
        var folder = MakeFolder();
        try
        {
            var url = new UriBuilder(Url(server, seed, size)) { Scheme = "ftps" }.Uri;
            var outcome = await new FtpTransferRunner().DownloadAsync(url, folder, Options(seed, size), ct);
            var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
            failed.ProbeFailure?.Kind.Should().Be(ProbeErrorKind.PermanentError);
            server.BytesSent.Should().Be(0);
            Directory.GetFiles(folder).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    [Trait("Requirement", "KASI-BWL-001")]
    public async Task FTP_data_reads_obey_the_shared_bandwidth_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "ftp-bandwidth";
        const int size = 1024 * 1024;
        await using var server = FtpTestServer.Start(seed, size);
        var folder = MakeFolder();
        try
        {
            var limiter = new BandwidthLimiter(256 * 1024);
            var time = TimeProvider.System;
            var started = time.GetTimestamp();
            var options = Options(seed, size) with { BandwidthLimiter = limiter };
            var outcome = await new FtpTransferRunner().DownloadAsync(Url(server, seed, size), folder, options, ct);
            var elapsed = time.GetElapsedTime(started);

            outcome.Should().BeOfType<TransferCompleted>();
            limiter.ConsumedBytes.Should().Be(size);
            elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(2));
            server.BytesSent.Should().Be(size);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Uri Url(FtpTestServer server, string seed, int size) =>
        new(server.BaseAddress, $"files/{seed}/{size}");

    private static HttpTransferOptions Options(string seed, int size) => new()
    {
        ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, GeneratedContent.Sha256Hex(seed, size)),
        RetryPolicy = new TransferRetryPolicy { MaxAttempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) },
    };

    private static string MakeFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-ftp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
