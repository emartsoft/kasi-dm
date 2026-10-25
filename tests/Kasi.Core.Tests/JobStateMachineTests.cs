namespace Kasi.Core.Tests;

[Trait("Requirement", "KASI-RES-004")]
[Trait("Requirement", "KASI-MED-004")]
public class JobStateMachineTests
{
    // Written out independently of the implementation: this table is the specification.
    private static readonly (JobState From, JobState To)[] _commonEdges =
    [
        (JobState.Queued, JobState.Probing),
        (JobState.Queued, JobState.Paused),
        (JobState.Probing, JobState.Downloading),
        (JobState.Probing, JobState.Failed),   // permanent error while probing (TRD 6.9 text)
        (JobState.Probing, JobState.Paused),
        (JobState.Downloading, JobState.Retrying),
        (JobState.Downloading, JobState.Paused),
        (JobState.Downloading, JobState.Failed), // permanent error mid-download, no retries (TC-RES-009)
        (JobState.Retrying, JobState.Downloading),
        (JobState.Retrying, JobState.Failed),
        (JobState.Retrying, JobState.Paused),
        (JobState.Paused, JobState.Downloading),
        (JobState.Paused, JobState.Queued),
        (JobState.Verifying, JobState.Completed),
        (JobState.Verifying, JobState.Failed),
        (JobState.Failed, JobState.Queued),
        (JobState.Completed, JobState.Queued),
    ];

    private static readonly (JobState From, JobState To)[] _directEdges = [(JobState.Downloading, JobState.Verifying)];

    private static readonly (JobState From, JobState To)[] _mediaEdges =
    [
        (JobState.Downloading, JobState.Muxing),
        (JobState.Muxing, JobState.Verifying),
        (JobState.Muxing, JobState.Failed),
    ];

    public static TheoryData<JobKind> AllKinds => new(Enum.GetValues<JobKind>());

    private static HashSet<(JobState, JobState)> Expected(JobKind kind) =>
        [.. _commonEdges, .. (kind == JobKind.Media ? _mediaEdges : _directEdges)];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Exactly_the_specified_transitions_are_legal(JobKind kind)
    {
        var expected = Expected(kind);

        foreach (var from in Enum.GetValues<JobState>())
        {
            foreach (var to in Enum.GetValues<JobState>())
            {
                JobStateMachine.CanTransition(kind, from, to).Should().Be(expected.Contains((from, to)), "{0} -> {1} for {2}", from, to, kind);
            }
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Next_states_have_no_duplicates_and_agree_with_CanTransition(JobKind kind)
    {
        foreach (var from in Enum.GetValues<JobState>())
        {
            var next = JobStateMachine.NextStates(kind, from);

            next.Should().OnlyHaveUniqueItems();
            next.Should().OnlyContain(to => JobStateMachine.CanTransition(kind, from, to));
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Completed_and_Failed_can_be_requeued(JobKind kind)
    {
        JobStateMachine.NextStates(kind, JobState.Completed).Should().Equal(JobState.Queued);
        JobStateMachine.NextStates(kind, JobState.Failed).Should().Equal(JobState.Queued);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_state_but_Completed_can_still_reach_Completed(JobKind kind)
    {
        // Muxing is not a state a non-media job can occupy, so it has no way out for them and is not checked.
        foreach (var start in Enum.GetValues<JobState>().Where(s => s != JobState.Completed && (s != JobState.Muxing || kind == JobKind.Media)))
        {
            Reachable(kind, start).Should().Contain(JobState.Completed, "a job in {0} must not be stuck", start);
        }
    }

    [Fact]
    public void Only_media_jobs_pass_through_Muxing_and_they_cannot_skip_it()
    {
        foreach (var kind in Enum.GetValues<JobKind>().Where(k => k != JobKind.Media))
        {
            Reachable(kind, JobState.Queued).Should().NotContain(JobState.Muxing);
            JobStateMachine.CanTransition(kind, JobState.Downloading, JobState.Verifying).Should().BeTrue();
        }

        JobStateMachine.CanTransition(JobKind.Media, JobState.Downloading, JobState.Verifying).Should().BeFalse();
        JobStateMachine.CanTransition(JobKind.Media, JobState.Downloading, JobState.Muxing).Should().BeTrue();
    }

    [Fact]
    public void An_undefined_state_is_a_programming_error()
    {
        var act = () => JobStateMachine.NextStates(JobKind.Http, (JobState)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Recovery_edges_are_separate_from_the_normal_state_machine()
    {
        var expected = new (JobState From, JobState To)[]
        {
            (JobState.Probing, JobState.Queued),
            (JobState.Downloading, JobState.Paused),
            (JobState.Downloading, JobState.Completed),
            (JobState.Downloading, JobState.Failed),
            (JobState.Retrying, JobState.Paused),
            (JobState.Retrying, JobState.Completed),
            (JobState.Retrying, JobState.Failed),
            (JobState.Verifying, JobState.Paused),
            (JobState.Verifying, JobState.Completed),
            (JobState.Verifying, JobState.Failed),
        };

        foreach (var from in Enum.GetValues<JobState>())
        {
            JobStateMachine.RecoveryNextStates(from).Should().Equal(expected.Where(e => e.From == from).Select(e => e.To));
            foreach (var to in Enum.GetValues<JobState>())
            {
                JobStateMachine.CanRecover(from, to).Should().Be(expected.Contains((from, to)), "{0} -> {1}", from, to);
            }
        }

        JobStateMachine.CanRecover(JobState.Downloading, JobState.Queued).Should().BeFalse();
        JobStateMachine.CanTransition(JobKind.Http, JobState.Probing, JobState.Queued).Should().BeFalse();
    }

    private static HashSet<JobState> Reachable(JobKind kind, JobState start)
    {
        var seen = new HashSet<JobState> { start };
        var queue = new Queue<JobState>([start]);
        while (queue.TryDequeue(out var state))
        {
            foreach (var next in JobStateMachine.NextStates(kind, state))
            {
                if (seen.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return seen;
    }
}
