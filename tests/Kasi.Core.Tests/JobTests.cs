namespace Kasi.Core.Tests;

[Trait("Requirement", "TRD-6.9")]
public class JobTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    private static Job NewJob(JobKind kind = JobKind.Http, JobState state = JobState.Queued) => new()
    {
        Id = JobId.New(new ManualTimeProvider(_start)),
        Kind = kind,
        Url = "https://example.com/file.bin",
        FileName = "file.bin",
        Folder = "C:/Downloads",
        QueueId = 1,
        QueuePos = 1.0,
        CreatedUtc = _start,
        State = state,
        DoneBytes = 123,
    };

    [Fact]
    public void A_new_job_starts_queued_and_resumable_with_no_error()
    {
        var job = NewJob();

        job.State.Should().Be(JobState.Queued);
        job.Resumable.Should().BeTrue();
        job.Error.Should().BeNull();
        job.CompletedUtc.Should().BeNull();
    }

    [Fact]
    public void A_legal_transition_returns_the_updated_job_and_leaves_the_original_alone()
    {
        var job = NewJob();

        var moved = job.TryTransition(JobState.Probing, TimeProvider.System, out var updated);

        moved.Should().BeTrue();
        updated.State.Should().Be(JobState.Probing);
        job.State.Should().Be(JobState.Queued);
        updated.DoneBytes.Should().Be(123);
    }

    [Fact]
    public void An_illegal_transition_is_refused_and_changes_nothing()
    {
        var job = NewJob();

        var moved = job.TryTransition(JobState.Completed, TimeProvider.System, out var updated);

        moved.Should().BeFalse();
        updated.Should().BeSameAs(job);
    }

    [Fact]
    public void Completing_stamps_the_time_and_clears_any_error()
    {
        var clock = new ManualTimeProvider(_start.AddMinutes(5));
        var job = NewJob(state: JobState.Verifying) with { Error = "stale" };

        job.TryTransition(JobState.Completed, clock, out var done).Should().BeTrue();

        done.CompletedUtc.Should().Be(_start.AddMinutes(5));
        done.Error.Should().BeNull();
    }

    [Fact]
    public void Failing_requires_a_message_and_keeps_it()
    {
        var job = NewJob(state: JobState.Verifying);

        var act = () => job.TryTransition(JobState.Failed, TimeProvider.System, out _);
        act.Should().Throw<ArgumentException>();
        job.TryTransition(JobState.Failed, TimeProvider.System, out var failed, "Verification failed").Should().BeTrue();

        failed.Error.Should().Be("Verification failed");
    }

    [Fact]
    public void Retrying_may_carry_the_last_transient_error_and_leaving_it_clears_the_error()
    {
        var job = NewJob(state: JobState.Downloading);

        job.TryTransition(JobState.Retrying, TimeProvider.System, out var retrying, "connection reset").Should().BeTrue();
        retrying.Error.Should().Be("connection reset");
        retrying.TryTransition(JobState.Downloading, TimeProvider.System, out var downloading).Should().BeTrue();

        downloading.Error.Should().BeNull();
    }

    [Fact]
    public void Retrying_a_failed_job_goes_back_to_Queued_keeping_the_saved_bytes_and_clearing_the_error()
    {
        var failed = NewJob(state: JobState.Failed) with { Error = "server said no" };

        failed.TryTransition(JobState.Queued, TimeProvider.System, out var queued).Should().BeTrue();

        queued.State.Should().Be(JobState.Queued);
        queued.Error.Should().BeNull();
        queued.DoneBytes.Should().Be(123);
    }

    [Fact]
    public void Media_jobs_must_mux_and_other_kinds_must_not()
    {
        var media = NewJob(JobKind.Media, JobState.Downloading);
        var http = NewJob(JobKind.Http, JobState.Downloading);

        media.TryTransition(JobState.Verifying, TimeProvider.System, out _).Should().BeFalse();
        media.TryTransition(JobState.Muxing, TimeProvider.System, out _).Should().BeTrue();
        http.TryTransition(JobState.Muxing, TimeProvider.System, out _).Should().BeFalse();
        http.TryTransition(JobState.Verifying, TimeProvider.System, out _).Should().BeTrue();
    }
}
