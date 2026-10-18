using System.Security.Cryptography;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Kasi.TestServer.Endpoints;
using Microsoft.Data.Sqlite;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-009")]
[Trait("Requirement", "KASI-ENG-010")]
public sealed class HttpTransferAuthAndRedirectTests : IAsyncLifetime
{
    private string _folder = null!;

    public ValueTask InitializeAsync()
    {
        _folder = Path.Combine(Path.GetTempPath(), "kasi-auth-redirect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    public async Task Sensitive_caller_headers_stop_at_a_cross_origin_redirect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await TestServerHost.StartAsync(cancellationToken: ct);
        await using var target = await TestServerHost.StartAsync(cancellationToken: ct);
        const int size = 2 * 1024 * 1024;
        var targetUrl = new UriBuilder(new Uri(target.BaseAddress, $"/files/cross-origin/{size}")) { Host = "localhost" }.Uri;
        var sourceUrl = new Uri(source.BaseAddress, "/redirect-to?target=" + Uri.EscapeDataString(targetUrl.AbsoluteUri));
        var options = new HttpTransferOptions
        {
            RequestHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Basic c2VjcmV0",
                ["Cookie"] = "session=cookie-value",
            },
            Secret = new JobSecret
            {
                Cookies = [new JobCookie { Name = "browser", Value = "browser-value", Domain = "127.0.0.1" }],
            },
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(sourceUrl, _folder, options, ct);

        await AssertCompletedHashAsync(outcome, "cross-origin", size, ct);
        source.Metrics.RequestsWithAuthorization.Should().BeGreaterThan(0);
        source.Metrics.RequestsWithCookie.Should().BeGreaterThan(0);
        target.Metrics.RequestsWithAuthorization.Should().Be(0);
        target.Metrics.RequestsWithCookie.Should().Be(0);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("digest")]
    [InlineData("cookie")]
    [Trait("TC", "TC-ENG-016")]
    public async Task Eight_connection_authenticated_downloads_match_SHA256(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        const int size = 4 * 1024 * 1024;
        var secret = mode == "cookie"
            ? new JobSecret { Cookies = [new JobCookie { Name = PathGates.SessionCookie, Value = "ok", Domain = "127.0.0.1", Path = "/auth/cookie" }] }
            : new JobSecret { Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = PathGates.BasicPassword } };
        var options = EightConnections() with { Secret = secret };

        var outcome = await new HttpTransferRunner().DownloadAsync(
            new Uri(host.BaseAddress, $"/auth/{mode}/files/{mode}/{size}"), _folder, options, ct);

        await AssertCompletedHashAsync(outcome, mode, size, ct);
        if (mode == "cookie")
        {
            host.Metrics.RequestsWithCookie.Should().BeGreaterThan(0);
        }
        else
        {
            host.Metrics.RequestsWithAuthorization.Should().BeGreaterThan(0);
            host.Metrics.AuthChallengesSent.Should().BeGreaterThan(1, "each new authenticated lane receives a challenge");
        }

        host.Metrics.TotalConnectionsAccepted.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("digest")]
    [InlineData("cookie")]
    [Trait("TC", "TC-ENG-016")]
    public async Task Missing_auth_fails_once_without_the_transfer_retry_loop(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);

        var outcome = await new HttpTransferRunner().DownloadAsync(
            new Uri(host.BaseAddress, $"/auth/{mode}/files/missing/1048576"), _folder, cancellationToken: ct);

        var failure = outcome.Should().BeOfType<TransferFailed>().Subject;
        failure.ProbeFailure!.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        host.Metrics.Http1RequestsServed.Should().Be(1);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("digest")]
    [Trait("TC", "TC-ENG-016")]
    public async Task Wrong_credentials_do_not_start_a_retry_loop(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        var options = new HttpTransferOptions
        {
            Secret = new JobSecret { Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = "wrong" } },
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(
            new Uri(host.BaseAddress, $"/auth/{mode}/files/wrong/1048576"), _folder, options, ct);

        outcome.Should().BeOfType<TransferFailed>().Which.ProbeFailure!.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        host.Metrics.Http1RequestsServed.Should().BeLessThan(5);
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    public async Task Handler_credentials_are_not_sent_to_a_redirected_origin()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var source = await TestServerHost.StartAsync(cancellationToken: ct);
        await using var target = await TestServerHost.StartAsync(cancellationToken: ct);
        var targetUrl = new UriBuilder(new Uri(target.BaseAddress, "/auth/basic/files/other-origin/1048576")) { Host = "localhost" }.Uri;
        var sourceUrl = new Uri(source.BaseAddress, "/redirect-to?target=" + Uri.EscapeDataString(targetUrl.AbsoluteUri));
        var options = new HttpTransferOptions
        {
            Secret = new JobSecret { Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = PathGates.BasicPassword } },
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(sourceUrl, _folder, options, ct);

        outcome.Should().BeOfType<TransferFailed>().Which.ProbeFailure!.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        target.Metrics.RequestsWithAuthorization.Should().Be(0);
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    public async Task Resuming_a_redirected_job_does_not_send_saved_credentials_to_the_final_origin()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Current-user DPAPI requires Windows.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using var source = await TestServerHost.StartAsync(cancellationToken: ct);
        await using var target = await TestServerHost.StartAsync(cancellationToken: ct);
        const int size = 1024 * 1024;
        var targetUrl = new UriBuilder(new Uri(target.BaseAddress, $"/auth/basic/files/resume-secret/{size}")) { Host = "localhost" }.Uri;
        var sourceUrl = new Uri(source.BaseAddress, "/redirect-to?target=" + Uri.EscapeDataString(targetUrl.AbsoluteUri));
        var factory = new SqliteConnectionFactory(Path.Combine(_folder, "resume.db"));
        await new MigrationRunner(factory).MigrateAsync(ct);
        var jobs = new JobRepository(factory);
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = sourceUrl.AbsoluteUri,
            FinalUrl = targetUrl.AbsoluteUri,
            FileName = "resume.bin",
            Folder = _folder,
            TotalBytes = size,
            ETag = $"\"resume-secret-{size}\"",
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
        };
        await jobs.AddAsync(job, ct);
        var secrets = new JobSecretRepository(factory);
        await secrets.SetAsync(job.Id, new JobSecret
        {
            Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = PathGates.BasicPassword },
        }, ct);
        var partPath = Path.Combine(_folder, job.FileName + PartFileWriter.PartExtension);
        await using (var part = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write))
        {
            part.SetLength(size);
        }

        await using var checkpoints = new CheckpointWriter(factory);
        checkpoints.Start();
        var options = new HttpTransferOptions
        {
            CheckpointJobId = job.Id,
            CheckpointWriter = checkpoints,
            SegmentRepository = new SegmentRepository(factory),
            JobRepository = jobs,
            SecretRepository = secrets,
            RequestHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Basic c2VjcmV0",
                ["Cookie"] = "source=session",
            },
        };

        var outcome = await new HttpTransferRunner().ResumeAsync(
            targetUrl, _folder, job.FileName, size, job.ETag, null, [new Segment(0, 0, size)], options, ct);

        outcome.Should().BeOfType<TransferFailed>();
        target.Metrics.RequestsWithAuthorization.Should().Be(0);
        target.Metrics.RequestsWithCookie.Should().Be(0);
        File.Exists(partPath).Should().BeTrue();
        (await secrets.GetAsync(job.Id, ct)).Should().NotBeNull();
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    public async Task Resuming_on_the_original_origin_uses_explicit_credentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        const string seed = "resume-digest";
        const int size = 2 * 1024 * 1024;
        const string fileName = "resume-digest.bin";
        var url = new Uri(host.BaseAddress, $"/auth/digest/files/{seed}/{size}");
        var partPath = Path.Combine(_folder, fileName + PartFileWriter.PartExtension);
        await using (var part = new FileStream(partPath, FileMode.CreateNew, FileAccess.Write))
        {
            part.SetLength(size);
        }

        var outcome = await new HttpTransferRunner().ResumeAsync(
            url,
            _folder,
            fileName,
            size,
            $"\"{seed}-{size}\"",
            null,
            [new Segment(0, 0, size)],
            new HttpTransferOptions
            {
                InitialUrl = url,
                Secret = new JobSecret
                {
                    Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = PathGates.BasicPassword },
                },
            },
            ct);

        await AssertCompletedHashAsync(outcome, seed, size, ct);
        host.Metrics.RequestsWithAuthorization.Should().BeGreaterThan(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [Trait("TC", "TC-ENG-017")]
    public async Task Redirect_chain_completes_and_all_leases_use_the_final_url(int hops)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        const int size = 2 * 1024 * 1024;

        var outcome = await new HttpTransferRunner().DownloadAsync(
            new Uri(host.BaseAddress, $"/redirect/{hops}/files/chain{hops}/{size}"),
            _folder,
            EightConnections() with { OverrideFileName = $"chain-{hops}.bin" },
            ct);

        var completed = await AssertCompletedHashAsync(outcome, $"chain{hops}", size, ct);
        completed.FinalUrl.AbsolutePath.Should().Be($"/redirect/0/files/chain{hops}/{size}");
        host.Metrics.Http1RequestsServed.Should().BeGreaterThan(hops + 1, "leases also request the final URL");
    }

    [Fact]
    [Trait("TC", "TC-ENG-017")]
    public async Task Eleven_redirects_fail_with_a_clear_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);

        var outcome = await new HttpTransferRunner().DownloadAsync(
            new Uri(host.BaseAddress, "/redirect/11/files/too-many/1048576"), _folder, cancellationToken: ct);

        var failure = outcome.Should().BeOfType<TransferFailed>().Subject;
        failure.ProbeFailure!.Kind.Should().Be(ProbeErrorKind.TooManyRedirects);
        failure.Detail.Should().Contain("maximum of 10 redirects");
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    [Trait("Requirement", "KASI-NFR-013")]
    public async Task Probe_failures_redact_cookies_passwords_headers_and_signed_url_queries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        var options = new HttpTransferOptions
        {
            RequestHeaders = new Dictionary<string, string> { ["Cookie"] = "raw-cookie-value" },
            Secret = new JobSecret
            {
                Cookies = [new JobCookie { Name = "session", Value = "browser-cookie-value", Domain = "127.0.0.1" }],
                Headers = new Dictionary<string, string> { ["Authorization"] = "secret-header-value" },
                Credentials = new HttpCredentials { UserName = "wrong", Password = "password-value" },
            },
        };
        var badTarget = Uri.EscapeDataString("ftp://example.com/file?token=target-secret");
        var paths = new[]
        {
            "/auth/basic/files/private/100?token=source-secret",
            "/redirect-to?target=" + badTarget,
            "/redirect/11/files/too-many/100?token=source-secret",
        };

        foreach (var path in paths)
        {
            var outcome = await new HttpTransferRunner().DownloadAsync(new Uri(host.BaseAddress, path), _folder, options, ct);
            var failure = outcome.Should().BeOfType<TransferFailed>().Subject;
            var message = failure.Detail + failure.ProbeFailure?.Message;
            foreach (var value in new[] { "raw-cookie-value", "browser-cookie-value", "secret-header-value", "password-value", "source-secret", "target-secret" })
            {
                message.Should().NotContain(value);
            }
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    public async Task Stored_secret_is_loaded_for_the_transfer_and_deleted_on_completion()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Current-user DPAPI requires Windows.");
            return;
        }
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        var url = new Uri(host.BaseAddress, "/auth/basic/files/stored/2097152");
        var factory = new SqliteConnectionFactory(Path.Combine(_folder, "kasi.db"));
        await new MigrationRunner(factory).MigrateAsync(ct);
        var jobs = new JobRepository(factory);
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = url.AbsoluteUri,
            FileName = "stored.bin",
            Folder = _folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
        };
        await jobs.AddAsync(job, ct);
        var secrets = new JobSecretRepository(factory);
        await secrets.SetAsync(job.Id, new JobSecret
        {
            Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = PathGates.BasicPassword },
        }, ct);
        await using var checkpoints = new CheckpointWriter(factory);
        checkpoints.Start();
        var options = EightConnections() with
        {
            OverrideFileName = "stored.bin",
            CheckpointJobId = job.Id,
            CheckpointWriter = checkpoints,
            SegmentRepository = new SegmentRepository(factory),
            JobRepository = jobs,
            SecretRepository = secrets,
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(url, _folder, options, ct);

        await AssertCompletedHashAsync(outcome, "stored", 2 * 1024 * 1024, ct);
        (await secrets.GetAsync(job.Id, ct)).Should().BeNull();
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    public async Task A_failed_secret_cleanup_does_not_turn_a_finished_download_into_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await TestServerHost.StartAsync(cancellationToken: ct);
        var url = new Uri(host.BaseAddress, "/files/cleanup/2097152");
        var factory = new SqliteConnectionFactory(Path.Combine(_folder, "cleanup.db"));
        await new MigrationRunner(factory).MigrateAsync(ct);
        var jobs = new JobRepository(factory);
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = url.AbsoluteUri,
            FileName = "cleanup.bin",
            Folder = _folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
        };
        await jobs.AddAsync(job, ct);
        await using var checkpoints = new CheckpointWriter(factory);
        checkpoints.Start();
        var options = EightConnections() with
        {
            OverrideFileName = "cleanup.bin",
            CheckpointJobId = job.Id,
            CheckpointWriter = checkpoints,
            SegmentRepository = new SegmentRepository(factory),
            JobRepository = jobs,
            SecretRepository = new UndeletableSecrets(),
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(url, _folder, options, ct);

        await AssertCompletedHashAsync(outcome, "cleanup", 2 * 1024 * 1024, ct);
    }

    private sealed class UndeletableSecrets : IJobSecretRepository
    {
        public Task SetAsync(JobId jobId, JobSecret secret, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<JobSecret?> GetAsync(JobId jobId, CancellationToken cancellationToken = default) => Task.FromResult<JobSecret?>(null);

        public Task<bool> DeleteAsync(JobId jobId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated database failure");
    }

    private static HttpTransferOptions EightConnections() => new()
    {
        SchedulerOptions = new SegmentSchedulerOptions { MaxConnections = 8, InitialConnections = 8 },
    };

    private static async Task<TransferCompleted> AssertCompletedHashAsync(TransferOutcome outcome, string seed, int size, CancellationToken ct)
    {
        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(completed.FinalPath, ct)));
        hash.Should().Be(GeneratedContent.Sha256Hex(seed, size));
        return completed;
    }
}
