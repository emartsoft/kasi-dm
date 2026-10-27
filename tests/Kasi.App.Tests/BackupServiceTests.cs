using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.Core;
using Kasi.Data;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-SYS-004")]
[Trait("TC", "TC-SYS-005")]
public sealed class BackupServiceTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasi-backup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
    private SqliteConnectionFactory _source = null!;
    private SqliteConnectionFactory _target = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _source = new SqliteConnectionFactory(Path.Combine(_root, "source.db"));
        _target = new SqliteConnectionFactory(Path.Combine(_root, "target.db"));
        await new MigrationRunner(_source).MigrateAsync(TestContext.Current.CancellationToken);
        await new MigrationRunner(_target).MigrateAsync(TestContext.Current.CancellationToken);
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
            // temp cleanup is best effort
        }
    }

    [Fact]
    public async Task Export_and_import_round_trip_settings_queues_profiles_and_jobs_without_secrets()
    {
        var ct = TestContext.Current.CancellationToken;
        var sourceBrowser = new BrowserSettingsStore(Path.Combine(_root, "source-browser.json"));
        var source = Service(_source, sourceBrowser);
        await sourceBrowser.SaveAsync(sourceBrowser.Current with { ClipboardCapture = true }, ct);
        var categories = new CategoryRepository(_source);
        await categories.AddAsync(new Category { Id = 0, Name = "Books", Folder = "Books" }, ct);
        var profiles = new SpeedProfileRepository(_source);
        var profileId = await profiles.AddAsync(new SpeedProfile { Id = 0, Name = "Night", LimitBps = 2_000_000, SortOrder = 0 }, ct);
        var schedules = new QueueScheduleRepository(_source);
        await schedules.UpsertAsync(new QueueSchedule
        {
            QueueId = 1,
            IsEnabled = true,
            StartTime = new TimeOnly(23, 0),
            DaysOfWeek = DaysOfWeek.All,
            ProfileId = profileId,
        }, ct);
        var jobs = new JobRepository(_source);
        await jobs.AddAsync(new Job
        {
            Id = JobId.New(_clock),
            Kind = JobKind.Http,
            Url = "https://example.test/file",
            FileName = "file.bin",
            Folder = _root,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = _clock.GetUtcNow(),
        }, ct);

        var backup = Path.Combine(_root, "backup.json");
        await source.ExportAsync(backup, ct);
        var text = await File.ReadAllTextAsync(backup, ct);
        text.Should().NotContain("cookie");
        text.Should().NotContain("password");

        var targetBrowser = new BrowserSettingsStore(Path.Combine(_root, "target-browser.json"));
        var target = Service(_target, targetBrowser);
        var result = await target.ImportAsync(backup, confirmed: true, ct);

        result.Imported.Should().BeTrue();
        (await categories.ListAsync(ct)).Should().Contain(category => category.Name == "Books");
        (await profiles.ListAsync(ct)).Should().Contain(profile => profile.Name == "Night" && profile.LimitBps == 2_000_000);
        (await schedules.GetAsync(1, ct))!.ProfileId.Should().NotBeNull();
        (await jobs.ListAsync(cancellationToken: ct)).Should().Contain(job => job.FileName == "file.bin");
        targetBrowser.Current.ClipboardCapture.Should().BeTrue();
    }

    [Fact]
    public async Task Import_requires_confirmation_and_rejects_a_newer_version()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = Service(_source, new BrowserSettingsStore(Path.Combine(_root, "browser.json")));
        var file = Path.Combine(_root, "backup.json");
        await service.ExportAsync(file, ct);

        (await service.ImportAsync(file, confirmed: false, ct)).Imported.Should().BeFalse();

        var future = Path.Combine(_root, "future.json");
        await File.WriteAllTextAsync(future, """{"version":2,"categories":[]}""", ct);
        Func<Task> act = () => service.ImportAsync(future, confirmed: true, ct);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    private static BackupService Service(SqliteConnectionFactory database, IBrowserSettings browser) => new(
        new CategoryRepository(database),
        new CategoryRuleRepository(database),
        new QueueRepository(database),
        new QueueScheduleRepository(database),
        new SpeedProfileRepository(database),
        new JobRepository(database),
        browser,
        new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero)));
}
