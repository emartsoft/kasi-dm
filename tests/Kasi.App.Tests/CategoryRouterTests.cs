using Kasi.App.Engine;
using Kasi.Core;
using Kasi.Data;

namespace Kasi.App.Tests;

/// <summary>M2.11b2: the second look at a job's category after the probe (KASI-CAT-001, TRD 10.4 rule 3).</summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-CAT-001")]
public sealed class CategoryRouterTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasi-router-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads = Path.Combine(Path.GetTempPath(), "Downloads");
    private JobRepository _jobs = null!;
    private CategoryRuleRepository _rules = null!;
    private CategoryRouter _router = null!;

    public async ValueTask InitializeAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_root, "kasi.db"));
        await new MigrationRunner(factory).MigrateAsync(TestContext.Current.CancellationToken);
        _jobs = new JobRepository(factory);
        _rules = new CategoryRuleRepository(factory);
        _router = new CategoryRouter(new CategoryRepository(factory), _rules, _jobs, _downloads);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the folder is in the temp directory
        }
    }

    private async Task<Job> AddAsync(string url, string fileName, bool auto = true, int? category = 1)
    {
        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = url,
            FileName = fileName,
            Folder = _downloads,
            QueueId = 1,
            QueuePos = 1,
            CategoryId = category,
            CategoryAuto = auto,
            CreatedUtc = DateTimeOffset.UnixEpoch,
        };
        await _jobs.AddAsync(job, TestContext.Current.CancellationToken);
        return job;
    }

    [Theory]
    [Trait("TC", "TC-CAT-001")]
    [InlineData("video/mp4", "Video", 5)]
    [InlineData("audio/mpeg", "Music", 4)]
    [InlineData("application/pdf", "Documents", 3)]
    [InlineData("application/zip", "Compressed", 2)]
    public async Task A_file_whose_name_says_nothing_goes_where_the_servers_MIME_type_says(string contentType, string folder, int category)
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AddAsync("https://files.example.org/get", "get");

        var routed = await _router.RouteAsync(job, "get", contentType, ct);

        routed.Should().Be(Path.Combine(_downloads, folder));
        var stored = (await _jobs.GetAsync(job.Id, ct))!;
        stored.CategoryId.Should().Be(category);
        stored.Folder.Should().Be(routed, "a resume finds the file where it was put");
        stored.CategoryAuto.Should().BeFalse("a job is routed once");
    }

    [Theory]
    [Trait("TC", "TC-CAT-001")]
    [InlineData("application/octet-stream")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_server_that_says_nothing_useful_leaves_the_job_where_it_is(string? contentType)
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AddAsync("https://files.example.org/get", "get");

        (await _router.RouteAsync(job, "get", contentType, ct)).Should().BeNull();

        var stored = (await _jobs.GetAsync(job.Id, ct))!;
        stored.Folder.Should().Be(_downloads);
        stored.CategoryId.Should().Be(1);
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task A_job_with_no_category_yet_is_not_moved_to_General_by_a_server_that_says_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var job = await AddAsync("https://files.example.org/get", "get", category: null);

        (await _router.RouteAsync(job, "get", "application/octet-stream", ct)).Should().BeNull();

        (await _jobs.GetAsync(job.Id, ct))!.CategoryAuto.Should().BeTrue("nothing was decided, so nothing was cleared");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_rule_still_decides_over_the_MIME_type()
    {
        var ct = TestContext.Current.CancellationToken;
        await _rules.AddAsync(new CategoryRule { CategoryId = 6, Kind = CategoryRuleKind.Domain, Pattern = "tools.example.org", Position = 1 }, ct);
        var job = await AddAsync("https://tools.example.org/get", "get");

        var routed = await _router.RouteAsync(job, "get", "video/mp4", ct);

        routed.Should().Be(Path.Combine(_downloads, "Programs"));
    }
}
