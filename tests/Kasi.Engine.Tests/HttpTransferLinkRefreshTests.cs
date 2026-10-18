using System.Net;
using System.Security.Cryptography;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;
using Kasi.TestServer.Endpoints;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-010")]
[Trait("TC", "TC-ENG-018")]
public sealed class HttpTransferLinkRefreshTests : IAsyncLifetime
{
    private const int FileSize = 32 * 1024 * 1024;
    private const string Seed = "sg";
    private const string FileName = "sg.bin";
    private string _folder = null!;

    public ValueTask InitializeAsync()
    {
        _folder = Path.Combine(Path.GetTempPath(), "kasi-link-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Expired_signed_link_refreshes_and_continues_from_saved_bytes(int redirectHops)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Current-user DPAPI requires Windows.");
        var ct = TestContext.Current.CancellationToken;
        var serverTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        await using var host = await TestServerHost.StartAsync(serverTime, ct);
        await using var paused = await PauseAfterExpiryAsync(host, serverTime, ct);
        host.ResetMetrics();
        var freshExpiry = serverTime.GetUtcNow().AddHours(1).ToUnixTimeSeconds();
        var path = $"/signed/files/{Seed}/{FileSize}?exp={freshExpiry}";
        if (redirectHops > 0)
        {
            path = $"/redirect/{redirectHops}/signed/files/{Seed}/{FileSize}?exp={freshExpiry}";
        }

        var refreshedUrl = new Uri(host.BaseAddress, path);
        var outcome = await new HttpTransferRunner().RefreshAndResumeAsync(
            paused.FinalUrl, refreshedUrl, _folder, FileName, FileSize, paused.ETag, paused.LastModified,
            paused.Segments, paused.Options with { RequestHeaders = null }, ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(completed.FinalPath, ct)));
        hash.Should().Be(GeneratedContent.Sha256Hex(Seed, FileSize));
        var saved = await paused.Jobs.GetAsync(paused.JobId, ct);
        saved!.Url.Should().Be(refreshedUrl.AbsoluteUri);
        saved.FinalUrl.Should().Be(completed.FinalUrl.AbsoluteUri);
        host.Metrics.RequestsServed.Should().BeGreaterThan(1);
        host.Metrics.BytesServed.Should().BeLessThanOrEqualTo(
            FileSize - paused.SavedBytes + paused.Options.SchedulerOptions.MaxEndgameDuplicateBytes + 1);
    }

    [Fact]
    public async Task Different_length_and_ETag_leave_the_part_file_and_checkpoint_untouched()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Current-user DPAPI requires Windows.");
        var ct = TestContext.Current.CancellationToken;
        var serverTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        await using var host = await TestServerHost.StartAsync(serverTime, ct);
        await using var paused = await PauseAfterExpiryAsync(host, serverTime, ct);
        var beforeHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(paused.PartPath, ct)));
        var beforeJob = (await paused.Jobs.GetAsync(paused.JobId, ct))!;
        var freshExpiry = serverTime.GetUtcNow().AddHours(1).ToUnixTimeSeconds();
        var candidates = new[]
        {
            new Uri(host.BaseAddress, $"/signed/files/{Seed}/{FileSize / 2}?exp={freshExpiry}"),
            new Uri(host.BaseAddress, $"/signed/files/different/{FileSize}?exp={freshExpiry}"),
        };

        foreach (var candidate in candidates)
        {
            host.ResetMetrics();
            var outcome = await new HttpTransferRunner().RefreshAndResumeAsync(
                paused.FinalUrl, candidate, _folder, FileName, FileSize, paused.ETag, paused.LastModified,
                paused.Segments, paused.Options with { RequestHeaders = null }, ct);

            var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
            failed.ErrorKind.Should().Be(TransferErrorKind.FileChanged);
            failed.SegmentsSnapshot.Should().BeEquivalentTo(paused.Segments);
            host.Metrics.BytesServed.Should().BeLessThanOrEqualTo(1, "validation asks for only byte zero");
            var afterHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(paused.PartPath, ct)));
            afterHash.Should().Be(beforeHash);
            var afterJob = (await paused.Jobs.GetAsync(paused.JobId, ct))!;
            afterJob.Url.Should().Be(beforeJob.Url);
            afterJob.FinalUrl.Should().Be(beforeJob.FinalUrl);
            (await paused.SegmentsRepository.LoadAsync(paused.JobId, ct)).Should().BeEquivalentTo(paused.Segments);
        }
    }

    [Fact]
    public async Task LastModified_validates_a_refreshed_link_when_the_saved_ETag_is_absent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Current-user DPAPI requires Windows.");
        var ct = TestContext.Current.CancellationToken;
        var serverTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        await using var host = await TestServerHost.StartAsync(serverTime, ct);
        await using var paused = await PauseAfterExpiryAsync(host, serverTime, ct);
        var job = (await paused.Jobs.GetAsync(paused.JobId, ct))!;
        (await paused.Jobs.UpdateAsync(job with { ETag = null }, ct)).Should().BeTrue();
        var freshExpiry = serverTime.GetUtcNow().AddHours(1).ToUnixTimeSeconds();
        var candidate = new Uri(host.BaseAddress, $"/signed/files/{Seed}/{FileSize}?exp={freshExpiry}");

        var outcome = await new HttpTransferRunner().RefreshAndResumeAsync(
            paused.FinalUrl, candidate, _folder, FileName, FileSize, null, paused.LastModified,
            paused.Segments, paused.Options with { RequestHeaders = null }, ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(completed.FinalPath, ct)))
            .Should().Be(GeneratedContent.Sha256Hex(Seed, FileSize));
    }

    [Fact]
    public async Task A_different_length_is_rejected_on_its_own_when_there_is_no_saved_ETag()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Current-user DPAPI requires Windows.");
        var ct = TestContext.Current.CancellationToken;
        var serverTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        await using var host = await TestServerHost.StartAsync(serverTime, ct);
        await using var paused = await PauseAfterExpiryAsync(host, serverTime, ct);
        var job = (await paused.Jobs.GetAsync(paused.JobId, ct))!;
        (await paused.Jobs.UpdateAsync(job with { ETag = null }, ct)).Should().BeTrue();
        var beforeJob = (await paused.Jobs.GetAsync(paused.JobId, ct))!;
        var freshExpiry = serverTime.GetUtcNow().AddHours(1).ToUnixTimeSeconds();
        // the test server's files share one Last-Modified, so only the length can tell this file from the paused one
        var shorter = new Uri(host.BaseAddress, $"/signed/files/{Seed}/{FileSize / 2}?exp={freshExpiry}");

        var outcome = await new HttpTransferRunner().RefreshAndResumeAsync(
            paused.FinalUrl, shorter, _folder, FileName, FileSize, null, paused.LastModified,
            paused.Segments, paused.Options with { RequestHeaders = null }, ct);

        outcome.Should().BeOfType<TransferFailed>().Which.ErrorKind.Should().Be(TransferErrorKind.FileChanged);
        var afterJob = (await paused.Jobs.GetAsync(paused.JobId, ct))!;
        afterJob.Url.Should().Be(beforeJob.Url);
        afterJob.FinalUrl.Should().Be(beforeJob.FinalUrl);
    }

    [Fact]
    public async Task Refreshed_link_on_another_origin_never_receives_the_old_secret()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Current-user DPAPI requires Windows.");
        var ct = TestContext.Current.CancellationToken;
        var serverTime = new FakeTimeProvider(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        await using var source = await TestServerHost.StartAsync(serverTime, ct);
        await using var target = await TestServerHost.StartAsync(cancellationToken: ct);
        await using var paused = await PauseAfterExpiryAsync(source, serverTime, ct);
        await paused.Options.SecretRepository!.SetAsync(paused.JobId, new JobSecret
        {
            Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = PathGates.BasicPassword },
            Headers = new Dictionary<string, string> { ["Authorization"] = "Basic old-header" },
            Cookies = [new JobCookie { Name = "old", Value = "old-cookie", Domain = "127.0.0.1" }],
        }, ct);
        var candidate = new UriBuilder(new Uri(target.BaseAddress, $"/auth/basic/files/{Seed}/{FileSize}")) { Host = "localhost" }.Uri;
        var options = paused.Options with
        {
            RequestHeaders = new Dictionary<string, string> { ["Authorization"] = "Basic old-caller" },
        };

        var blocked = await new HttpTransferRunner().RefreshAndResumeAsync(
            paused.FinalUrl, candidate, _folder, FileName, FileSize, paused.ETag, paused.LastModified,
            paused.Segments, options, ct);

        blocked.Should().BeOfType<TransferFailed>().Which.ProbeFailure!.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        target.Metrics.RequestsWithAuthorization.Should().Be(0);
        target.Metrics.RequestsWithCookie.Should().Be(0);
        (await paused.Jobs.GetAsync(paused.JobId, ct))!.Url.Should().NotBe(candidate.AbsoluteUri);

        target.ResetMetrics();
        var newSecret = new JobSecret
        {
            Credentials = new HttpCredentials { UserName = PathGates.BasicUser, Password = PathGates.BasicPassword },
        };
        var outcome = await new HttpTransferRunner().RefreshAndResumeAsync(
            paused.FinalUrl, candidate, _folder, FileName, FileSize, paused.ETag, paused.LastModified,
            paused.Segments, options with { Secret = newSecret }, ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(completed.FinalPath, ct)))
            .Should().Be(GeneratedContent.Sha256Hex(Seed, FileSize));
        target.Metrics.RequestsWithAuthorization.Should().BeGreaterThan(0);
        target.Metrics.RequestsWithCookie.Should().Be(0);
    }

    private async Task<PausedJob> PauseAfterExpiryAsync(TestServerHost host, FakeTimeProvider serverTime, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Current-user DPAPI requires Windows.");
        }

        var expiry = serverTime.GetUtcNow().AddMinutes(1).ToUnixTimeSeconds();
        var url = new Uri(host.BaseAddress, $"/signed/files/{Seed}/{FileSize}?exp={expiry}");
        var factory = new SqliteConnectionFactory(Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".db"));
        await new MigrationRunner(factory).MigrateAsync(ct);
        var jobs = new JobRepository(factory);
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = url.AbsoluteUri,
            FileName = FileName,
            Folder = _folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
        };
        await jobs.AddAsync(job, ct);
        var segments = new SegmentRepository(factory);
        var secrets = new JobSecretRepository(factory);
#pragma warning disable CA2000 // PausedJob owns the writer after this helper returns
        var writer = new CheckpointWriter(factory);
#pragma warning restore CA2000
        writer.Start();
        var chaosId = host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 });
        var options = new HttpTransferOptions
        {
            OverrideFileName = FileName,
            SchedulerOptions = new SegmentSchedulerOptions
            {
                MaxConnections = 4,
                InitialConnections = 4,
                MinLeaseBytes = 512 * 1024,
                MaxLeaseBytes = 512 * 1024,
            },
            CheckpointJobId = job.Id,
            CheckpointWriter = writer,
            SegmentRepository = segments,
            JobRepository = jobs,
            SecretRepository = secrets,
            CheckpointInterval = TimeSpan.FromMilliseconds(100),
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };

        var download = new HttpTransferRunner().DownloadAsync(url, _folder, options, ct);
        await EventuallyAsync(async () => (await segments.LoadAsync(job.Id, ct)).Sum(s => s.Written) >= FileSize / 4, ct);
        serverTime.Advance(TimeSpan.FromMinutes(2));
        var failed = (await download).Should().BeOfType<TransferFailed>().Subject;
        failed.HttpStatusCode.Should().Be(HttpStatusCode.Forbidden);
        options.RetryPolicy.Classify(failed.ErrorKind, failed.HttpStatusCode).Should().Be(TransferRetryClass.AuthRequired);
        host.Metrics.SignedLinkRejects.Should().BeInRange(1, options.SchedulerOptions.MaxConnections);
        var rows = await segments.LoadAsync(job.Id, ct);
        var savedBytes = rows.Sum(s => s.Written);
        savedBytes.Should().BeGreaterThan(0).And.BeLessThan(FileSize);
        var savedJob = (await jobs.GetAsync(job.Id, ct))!;
        var partPath = Path.Combine(_folder, FileName + PartFileWriter.PartExtension);
        File.Exists(partPath).Should().BeTrue();
        return new PausedJob(job.Id, jobs, segments, writer, options, new Uri(savedJob.FinalUrl!),
            savedJob.ETag, savedJob.LastModified, rows, savedBytes, partPath);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!await condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed record PausedJob(
        JobId JobId,
        JobRepository Jobs,
        SegmentRepository SegmentsRepository,
        CheckpointWriter Writer,
        HttpTransferOptions Options,
        Uri FinalUrl,
        string? ETag,
        string? LastModified,
        IReadOnlyList<Segment> Segments,
        long SavedBytes,
        string PartPath) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Writer.DisposeAsync();
    }
}
