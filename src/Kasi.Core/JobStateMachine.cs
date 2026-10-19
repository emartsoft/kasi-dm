namespace Kasi.Core;

/// <summary>
/// The legal job state transitions (TRD 6.9, ADR 0002). Pure and allocation-light enough for the engine to ask on every
/// change; it never throws for an illegal move, the caller gets <c>false</c> and decides (CONTRIBUTING 3.1).
/// </summary>
public static class JobStateMachine
{
    public static bool CanTransition(JobKind kind, JobState from, JobState to) => NextStates(kind, from).Contains(to);

    /// <summary>
    /// Recovery-only edges used by start-up recovery. They are deliberately outside <see cref="CanTransition"/> so normal
    /// operations cannot pretend a crash happened (ADR 0002).
    /// </summary>
    public static IReadOnlyList<JobState> RecoveryNextStates(JobState from) => from switch
    {
        JobState.Probing => [JobState.Queued],
        // a crash after the final rename completes the job; a mismatching published checksum fails it; otherwise it is paused
        JobState.Downloading or JobState.Retrying or JobState.Verifying => [JobState.Paused, JobState.Completed, JobState.Failed],
        _ => [],
    };

    public static bool CanRecover(JobState from, JobState to) => RecoveryNextStates(from).Contains(to);

    /// <summary>
    /// States reachable from <paramref name="from"/>. A pause can come from any active state and resume goes back to
    /// <see cref="JobState.Downloading"/> or, for a job paused before it started, to <see cref="JobState.Queued"/>;
    /// which one applies depends on where the pause happened, so the engine chooses.
    /// </summary>
    public static IReadOnlyList<JobState> NextStates(JobKind kind, JobState from) => from switch
    {
        JobState.Queued => [JobState.Probing, JobState.Paused],
        JobState.Probing => [JobState.Downloading, JobState.Failed, JobState.Paused],
        JobState.Downloading => [kind == JobKind.Media ? JobState.Muxing : JobState.Verifying, JobState.Retrying, JobState.Paused, JobState.Failed],
        JobState.Retrying => [JobState.Downloading, JobState.Failed, JobState.Paused],
        JobState.Paused => [JobState.Downloading, JobState.Queued],
        JobState.Muxing => kind == JobKind.Media ? [JobState.Verifying, JobState.Failed] : [], // only media jobs ever occupy Muxing
        JobState.Verifying => [JobState.Completed, JobState.Failed],
        JobState.Failed => [JobState.Queued],
        JobState.Completed => [],
        _ => throw new ArgumentOutOfRangeException(nameof(from), from, "Not a defined job state."),
    };
}
