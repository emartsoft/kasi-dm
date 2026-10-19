using Kasi.Core;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-013")]
[Trait("TC", "TC-ENG-021")]
public sealed class DuplicateDetectorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kasi-duplicate-" + Guid.NewGuid().ToString("N"));

    public DuplicateDetectorTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Same_url_after_origin_normalisation_offers_resume_when_part_exists()
    {
        var job = MakeJob("https://example.test/file.bin?x=1#old", "file.bin");
        await File.WriteAllBytesAsync(Path.Combine(_folder, "file.bin.kasipart"), [1, 2, 3], TestContext.Current.CancellationToken);

        var result = await DuplicateDetector.CheckAsync(
            new Jobs([job]),
            new Uri("HTTPS://EXAMPLE.TEST:443/file.bin?x=1#new"),
            _folder,
            "file.bin",
            TestContext.Current.CancellationToken);

        result.ExistingJob.Should().Be(job);
        result.HasConflict.Should().BeTrue();
        result.PartFileExistsOnDisk.Should().BeTrue();
        result.AvailableActions.Should().ContainInOrder(DuplicateAction.Resume, DuplicateAction.Overwrite, DuplicateAction.AutoRename);
        result.SuggestedFileName.Should().Be("file (1).bin");
    }

    [Fact]
    public async Task Changed_signed_query_matches_filename_but_cannot_resume_the_old_url()
    {
        var job = MakeJob("https://example.test/file.bin?exp=1", "file.bin");
        await File.WriteAllBytesAsync(Path.Combine(_folder, "file.bin.kasipart"), [1], TestContext.Current.CancellationToken);

        var result = await DuplicateDetector.CheckAsync(
            new Jobs([job]),
            new Uri("https://example.test/file.bin?exp=2"),
            _folder,
            "file.bin",
            TestContext.Current.CancellationToken);

        result.ExistingJob.Should().Be(job);
        result.AvailableActions.Should().NotContain(DuplicateAction.Resume);
        result.AvailableActions.Should().Contain(DuplicateAction.Overwrite).And.Contain(DuplicateAction.AutoRename);
    }

    [Fact]
    public async Task Existing_disk_file_part_and_job_names_are_skipped_by_auto_rename()
    {
        await File.WriteAllBytesAsync(Path.Combine(_folder, "archive.tar.gz"), [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(_folder, "archive (1).tar.gz.kasipart"), [1], TestContext.Current.CancellationToken);
        var job = MakeJob("https://example.test/other", "archive (2).tar.gz");

        var result = await DuplicateDetector.CheckAsync(
            new Jobs([job]),
            new Uri("https://example.test/archive"),
            _folder,
            "archive.tar.gz",
            TestContext.Current.CancellationToken);

        result.TargetFileExistsOnDisk.Should().BeTrue();
        result.SuggestedFileName.Should().Be("archive (3).tar.gz");
    }

    [Fact]
    public async Task Suggested_name_keeps_the_extension_inside_the_200_character_limit()
    {
        var longName = new string('a', 193) + ".tar.gz";
        await File.WriteAllBytesAsync(Path.Combine(_folder, longName), [1], TestContext.Current.CancellationToken);

        var result = await DuplicateDetector.CheckAsync(
            new Jobs([]),
            new Uri("https://example.test/long"),
            _folder,
            longName,
            TestContext.Current.CancellationToken);

        result.SuggestedFileName.Length.Should().Be(FileNameSanitiser.MaxLength);
        result.SuggestedFileName.Should().EndWith(" (1).tar.gz");
    }

    [Fact]
    public async Task Automatic_rename_does_not_split_a_surrogate_pair_at_the_length_cap()
    {
        var name = new string('a', 188) + "😀.tar.gz";
        await File.WriteAllBytesAsync(Path.Combine(_folder, name), [1], TestContext.Current.CancellationToken);

        var result = await DuplicateDetector.CheckAsync(
            new Jobs([]),
            new Uri("https://example.test/emoji"),
            _folder,
            name,
            TestContext.Current.CancellationToken);

        result.SuggestedFileName.Should().Be(new string('a', 188) + " (1).tar.gz");
    }

    [Fact]
    public async Task Completed_job_does_not_offer_resume_and_a_new_target_has_no_actions()
    {
        var completed = MakeJob("https://example.test/file", "old.bin") with { State = JobState.Completed };
        var duplicate = await DuplicateDetector.CheckAsync(
            new Jobs([completed]),
            new Uri("https://example.test/file"),
            _folder,
            "new.bin",
            TestContext.Current.CancellationToken);
        duplicate.AvailableActions.Should().NotContain(DuplicateAction.Resume);

        var fresh = await DuplicateDetector.CheckAsync(
            new Jobs([completed]),
            new Uri("https://example.test/new"),
            _folder,
            "fresh.bin",
            TestContext.Current.CancellationToken);
        fresh.HasConflict.Should().BeFalse();
        fresh.AvailableActions.Should().BeEmpty();
        fresh.SuggestedFileName.Should().Be("fresh.bin");
    }

    [Fact]
    public async Task User_information_in_a_stored_url_does_not_hide_a_duplicate()
    {
        var job = MakeJob("ftp://user@example.test/pub/file.bin", "file.bin");

        var result = await DuplicateDetector.CheckAsync(
            new Jobs([job]), new Uri("ftp://example.test/pub/file.bin"), _folder, "other.bin", TestContext.Current.CancellationToken);

        result.ExistingJob.Should().Be(job);
    }

    [Fact]
    public async Task A_stored_job_with_an_unusable_path_is_skipped_instead_of_failing_the_check()
    {
        var broken = MakeJob("https://example.test/a.bin", "bad|name?.bin") with { Folder = "C:\\no\0pe" };
        var good = MakeJob("https://example.test/b.bin", "b.bin");

        var result = await DuplicateDetector.CheckAsync(
            new Jobs([broken, good]), new Uri("https://example.test/b.bin"), _folder, "b.bin", TestContext.Current.CancellationToken);

        result.ExistingJob.Should().Be(good);
    }

    private Job MakeJob(string url, string name) => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = JobKind.Http,
        Url = url,
        FileName = name,
        Folder = _folder,
        QueueId = 1,
        QueuePos = 1,
        CreatedUtc = TimeProvider.System.GetUtcNow(),
    };

    private sealed class Jobs(IReadOnlyList<Job> jobs) : IJobRepository
    {
        public Task<IReadOnlyList<Job>> ListAsync(int? queueId = null, JobState? state = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(jobs);

        public Task AddAsync(Job job, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UpdateTransferMetadataAsync(JobId id, JobTransferMetadata metadata, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateUrlAsync(JobId id, string url, string? finalUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
