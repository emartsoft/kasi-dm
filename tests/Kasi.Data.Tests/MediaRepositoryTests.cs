using Kasi.Core;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-MED-003")]
public sealed class MediaRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly JobId _jobId = JobId.New(TimeProvider.System);
    private MediaRepository _media = null!;

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);
        var jobs = new JobRepository(_db.Factory);
        await jobs.AddAsync(new Job
        {
            Id = _jobId,
            Kind = JobKind.Media,
            Url = "https://media.example/master.m3u8",
            FileName = "capture.mp4",
            Folder = "C:/media",
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
        }, TempDatabase.Ct);
        _media = new MediaRepository(_db.Factory);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Replace_load_and_batched_completion_update_the_job_progress()
    {
        await _media.ReplaceAsync(_jobId, Streams(), Segments(), 3000, TempDatabase.Ct);

        (await _media.LoadStreamsAsync(_jobId, TempDatabase.Ct)).Should().HaveCount(2);
        var segments = await _media.LoadSegmentsAsync(_jobId, TempDatabase.Ct);
        segments.Should().HaveCount(3);
        segments.Should().OnlyContain(segment => !segment.Completed);
        segments[0].RangeStart.Should().Be(0);
        segments[0].RangeEnd.Should().Be(99);
        (await _db.ScalarAsync($"SELECT done_bytes FROM job WHERE id = '{_jobId}';")).Should().Be(0L);
        (await _db.ScalarAsync($"SELECT total_bytes FROM job WHERE id = '{_jobId}';")).Should().Be(3000L);

        await _media.MarkCompletedAsync(
            _jobId,
            [new MediaSegmentCompletion(0, 0, 100), new MediaSegmentCompletion(1, 0, 50)],
            3000,
            TempDatabase.Ct);

        (await _media.LoadSegmentsAsync(_jobId, TempDatabase.Ct)).Count(segment => segment.Completed).Should().Be(2);
        (await _db.ScalarAsync($"SELECT done_bytes FROM job WHERE id = '{_jobId}';")).Should().Be(150L);

        await _media.ResetCompletionAsync(_jobId, TempDatabase.Ct);
        (await _media.LoadSegmentsAsync(_jobId, TempDatabase.Ct)).Should().OnlyContain(segment => !segment.Completed);
        (await _db.ScalarAsync($"SELECT done_bytes FROM job WHERE id = '{_jobId}';")).Should().Be(0L);
    }

    [Fact]
    public async Task Deleting_the_job_cascades_to_media_rows()
    {
        await _media.ReplaceAsync(_jobId, Streams(), Segments(), 3000, TempDatabase.Ct);

        (await new JobRepository(_db.Factory).DeleteAsync(_jobId, TempDatabase.Ct)).Should().BeTrue();

        (await _media.LoadStreamsAsync(_jobId, TempDatabase.Ct)).Should().BeEmpty();
        (await _media.LoadSegmentsAsync(_jobId, TempDatabase.Ct)).Should().BeEmpty();
    }

    private static IReadOnlyList<MediaStreamRecord> Streams() =>
    [
        new(0, "video", "en", "avc1", "mp4", "video-720p", 2500, "https://media.example/init.mp4", 100),
        new(1, "audio", "en", "mp4a", "mp4", "audio-en", 500),
    ];

    private static IReadOnlyList<MediaSegmentRecord> Segments() =>
    [
        new(0, 0, "https://media.example/seg-1.m4s", 100, Iv: "0x01", Discontinuity: false, RangeStart: 0, RangeEnd: 99),
        new(0, 1, "https://media.example/seg-2.m4s", 200),
        new(1, 0, "https://media.example/audio-1.m4s", 50),
    ];
}
