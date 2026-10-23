using Kasi.App.Engine;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;

namespace Kasi.App.Tests;

/// <summary>The real facade over the real engine and a temporary database; no network (jobs are added, not started).</summary>
[Trait("Requirement", "KASI-UI-002")]
public sealed class JobServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasi-jobservice-" + Guid.NewGuid().ToString("N"));
    private CheckpointWriter _checkpoints = null!;
    private DownloadEngine _engine = null!;
    private JobService _service = null!;
    private JobSecretRepository _secrets = null!;
    private BandwidthLimiter _limiter = null!;

    public async ValueTask InitializeAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_root, "kasi.db"));
        await new MigrationRunner(factory).MigrateAsync(TestContext.Current.CancellationToken);
        _checkpoints = new CheckpointWriter(factory);
        _checkpoints.Start();
        var jobs = new JobRepository(factory);
        _secrets = new JobSecretRepository(factory);
        _engine = new DownloadEngine(jobs, new SegmentRepository(factory), _checkpoints, _limiter = new BandwidthLimiter());
        await _engine.StartAsync(TestContext.Current.CancellationToken);
        _service = new JobService(_engine, jobs, new CategoryRepository(factory), new QueueRepository(factory), Path.Combine(_root, "Downloads"), _secrets, _limiter);
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _checkpoints.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the folder is in the temp directory
        }
    }

    private static NewDownload Request(string name = "pack.zip", int? category = 2, string url = "https://files.example.org/pack.zip") =>
        new(url, name, QueueId: 1, CategoryId: category, StartNow: false);

    [Fact]
    public async Task The_default_queue_and_categories_are_there_after_migration()
    {
        var ct = TestContext.Current.CancellationToken;

        (await _service.QueuesAsync(ct)).Should().ContainSingle(q => q.Id == 1);
        (await _service.CategoriesAsync(ct)).Select(c => c.Name).Should().StartWith(["General", "Compressed"]);
    }

    [Fact]
    public async Task An_added_job_is_stored_queued_in_its_category_folder_and_announced()
    {
        var ct = TestContext.Current.CancellationToken;
        var announced = new List<EngineEvent>();
        _service.EventRaised += announced.Add;

        var result = await _service.AddAsync(Request(), cancellationToken: ct);

        result.Job.Should().NotBeNull();
        result.Job!.State.Should().Be(JobState.Queued);
        result.Job.Folder.Should().Be(Path.Combine(_root, "Downloads", "Compressed"));
        result.Job.CategoryId.Should().Be(2);
        result.Job.Kind.Should().Be(JobKind.Http);
        (await _service.ListAsync(ct)).Should().ContainSingle(j => j.Id == result.Job.Id);
        await WaitUntilAsync(() => announced.OfType<JobAdded>().Any(a => a.Job.Id == result.Job.Id));
    }

    [Fact]
    public async Task General_saves_straight_into_downloads_and_an_explicit_folder_wins()
    {
        var ct = TestContext.Current.CancellationToken;

        var general = await _service.AddAsync(Request("a.bin", category: 1, url: "https://files.example.org/a.bin"), cancellationToken: ct);
        var explicitFolder = await _service.AddAsync(Request("b.bin", url: "https://files.example.org/b.bin") with { Folder = Path.Combine(_root, "Elsewhere") }, cancellationToken: ct);

        general.Job!.Folder.Should().Be(Path.Combine(_root, "Downloads"));
        explicitFolder.Job!.Folder.Should().Be(Path.Combine(_root, "Elsewhere"));
    }

    [Fact]
    public async Task Jobs_keep_the_order_they_were_added_in_their_queue()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await _service.AddAsync(Request("one.zip", url: "https://files.example.org/one.zip"), cancellationToken: ct);
        var second = await _service.AddAsync(Request("two.zip", url: "https://files.example.org/two.zip"), cancellationToken: ct);

        second.Job!.QueuePos.Should().BeGreaterThan(first.Job!.QueuePos);
        (await _service.ListAsync(ct)).Select(j => j.FileName).Should().Equal("one.zip", "two.zip");
    }

    [Fact]
    public async Task A_duplicate_is_reported_and_nothing_is_added_until_the_caller_decides()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.AddAsync(Request(), cancellationToken: ct);

        var again = await _service.AddAsync(Request(), cancellationToken: ct);

        again.Job.Should().BeNull();
        again.Check.HasConflict.Should().BeTrue();
        (await _service.ListAsync(ct)).Should().HaveCount(1);
    }

    [Fact]
    public async Task Auto_rename_adds_the_job_under_the_suggested_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await _service.AddAsync(Request(), cancellationToken: ct);

        var again = await _service.AddAsync(Request(), DuplicateAction.AutoRename, ct);

        again.Job.Should().NotBeNull();
        again.Job!.FileName.Should().Be(again.Check.SuggestedFileName).And.NotBe("pack.zip");
        (await _service.ListAsync(ct)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData("file:///c:/secret.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    public async Task Only_web_and_ftp_links_can_be_added(string url)
    {
        var act = () => _service.AddAsync(Request(url: url), cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
        (await _service.ListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cookies_and_the_agent_are_stored_with_the_job_and_nowhere_else()
    {
        var ct = TestContext.Current.CancellationToken;
        var cookie = new JobCookie { Name = "sid", Value = "COOKIE-VALUE", Domain = ".example.org" };

        var result = await _service.AddAsync(Request() with { Cookies = [cookie], UserAgent = "Agent/1.0" }, cancellationToken: ct);

        var secret = await _secrets.GetAsync(result.Job!.Id, ct);
        secret!.Cookies.Should().ContainSingle(c => c.Name == "sid" && c.Value == "COOKIE-VALUE");
        secret.Headers["User-Agent"].Should().Be("Agent/1.0");
        result.Job.ToString().Should().NotContain("COOKIE-VALUE");
    }

    [Fact]
    public async Task A_job_without_cookies_or_agent_has_no_secret()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _service.AddAsync(Request(), cancellationToken: ct);

        (await _secrets.GetAsync(result.Job!.Id, ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_checksum_is_stored_normalised_and_a_wrong_length_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var hex = new string('A', 64);

        var result = await _service.AddAsync(Request() with { ChecksumAlgorithm = "SHA-256", Checksum = hex }, cancellationToken: ct);
        var wrong = () => _service.AddAsync(Request("b.zip", url: "https://files.example.org/b.zip") with { ChecksumAlgorithm = "md5", Checksum = hex }, cancellationToken: ct);

        result.Job!.ChecksumAlgo.Should().Be("sha256");
        result.Job.Checksum.Should().Be(hex.ToLowerInvariant());
        await wrong.Should().ThrowAsync<ArgumentException>();
        (await _service.ListAsync(ct)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_link_refresh_for_a_job_that_is_not_paused_is_refused_without_touching_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var added = await _service.AddAsync(Request(), cancellationToken: ct);

        var result = await _service.RefreshLinkAsync(added.Job!.Id, "https://files.example.org/new.zip", [], ct);

        result.Problem.Should().Be(RefreshProblem.NotResumable);
        (await _service.GetAsync(added.Job.Id, ct))!.State.Should().Be(JobState.Queued);
    }

    [Theory]
    [InlineData("ftp://files.example.org/new.zip")]
    [InlineData("not a url")]
    public async Task A_refreshed_link_must_be_http_or_https(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        var added = await _service.AddAsync(Request(), cancellationToken: ct);

        var act = () => _service.RefreshLinkAsync(added.Job!.Id, url, [], ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void The_speed_limit_is_the_engines_shared_cap()
    {
        _service.SpeedLimit.Should().BeNull();

        _service.SpeedLimit = 5_000_000;
        _limiter.GlobalBytesPerSecond.Should().Be(5_000_000);
        _service.SpeedLimit = null;

        _limiter.GlobalBytesPerSecond.Should().BeNull();
    }

    [Fact]
    public async Task Pause_all_and_resume_all_leave_jobs_that_are_neither_running_nor_paused_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        var added = await _service.AddAsync(Request(), cancellationToken: ct);

        await _service.PauseAllAsync(ct);
        await _service.ResumeAllAsync(ct);

        (await _service.GetAsync(added.Job!.Id, ct))!.State.Should().Be(JobState.Queued);
    }

    [Fact]
    public async Task An_ftp_link_becomes_an_ftp_job()
    {
        var result = await _service.AddAsync(Request(url: "ftp://files.example.org/pack.zip"), cancellationToken: TestContext.Current.CancellationToken);

        result.Job!.Kind.Should().Be(JobKind.Ftp);
    }

    [Fact]
    public async Task Cancelling_removes_the_job_and_announces_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var removed = new List<JobId>();
        _service.EventRaised += e =>
        {
            if (e is JobRemoved r)
            {
                lock (removed)
                {
                    removed.Add(r.Id);
                }
            }
        };
        var added = await _service.AddAsync(Request(), cancellationToken: ct);

        await _service.CancelAsync(added.Job!.Id, cancellationToken: ct);

        (await _service.ListAsync(ct)).Should().BeEmpty();
        await WaitUntilAsync(() =>
        {
            lock (removed)
            {
                return removed.Contains(added.Job.Id);
            }
        });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30)); // generous: events come from the engine's own dispatcher
        while (!condition())
        {
            await Task.Delay(20, limit.Token);
        }
    }
}
