using System.Collections.Concurrent;
using Kasi.Core;
using Kasi.Data;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;
using Microsoft.Data.Sqlite;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-NFR-019")]
[Trait("Requirement", "KASI-ENG-008")]
public sealed class DownloadEngineLifecycleTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _root = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _root = Path.Combine(Path.GetTempPath(), "kasi-engine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        finally
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task A_headless_download_persists_each_transition_and_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/engine-download/4194304", "engine-download.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        var states = new ConcurrentQueue<(JobState From, JobState To)>();
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobStateChanged changed && changed.Id == job.Id)
            {
                states.Enqueue((changed.From, changed.To));
            }
        };

        await harness.Engine.StartAndWaitAsync(job.Id, ct);
        await harness.Engine.StopAsync(ct);
        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.Error.Should().BeNull($"the engine stored state {stored.State}");

        states.Should().ContainInOrder(
            (JobState.Queued, JobState.Probing),
            (JobState.Probing, JobState.Downloading),
            (JobState.Downloading, JobState.Verifying),
            (JobState.Verifying, JobState.Completed));
        stored.State.Should().Be(JobState.Completed);
        stored.DoneBytes.Should().Be(4 * 1024 * 1024);
        stored.CompletedUtc.Should().NotBeNull();
        (await Sha256FileAsync(Path.Combine(job.Folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("engine-download", 4 * 1024 * 1024));
    }

    [Fact]
    public async Task A_state_changed_handler_sees_the_committed_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/handler-row/2097152", "handler-row.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        var seen = new TaskCompletionSource<JobState>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobStateChanged { To: JobState.Completed } changed && changed.Id == job.Id)
            {
                _ = Task.Run(async () => seen.TrySetResult((await harness.Jobs.GetAsync(job.Id, ct))!.State), ct);
            }
        };

        await harness.Engine.StartAndWaitAsync(job.Id, ct);

        (await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), ct)).Should().Be(JobState.Completed);
        await harness.Engine.StopAsync(ct);
    }

    [Fact]
    public async Task A_throwing_subscriber_is_counted_and_does_not_stop_the_transfer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/throwing-subscriber/2097152", "throwing-subscriber.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.EventRaised += _ => throw new InvalidOperationException("subscriber failed");

        await harness.Engine.StartAndWaitAsync(job.Id, ct);
        await harness.Engine.StopAsync(ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
        harness.Engine.SubscriberFaultCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_duplicate_add_returns_the_conflict_without_adding_another_job()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/duplicate-add/1048576", "duplicate-add.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);

        var duplicate = job with { Id = JobId.New(TimeProvider.System) };
        var check = await harness.Engine.AddJobAsync(duplicate, cancellationToken: ct);

        check.HasConflict.Should().BeTrue();
        check.ExistingJob!.Id.Should().Be(job.Id);
        (await harness.Jobs.ListAsync(cancellationToken: ct)).Should().ContainSingle();
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task Resume_asked_the_moment_a_job_shows_Paused_waits_for_the_pause_to_finish_instead_of_failing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/pause-then-resume/8388608", "pause-then-resume.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { State: JobState.Downloading, DoneBytes: > 0 }, ct);
        // hold the pause at its last step: the row already says Paused, the runner is not yet gone (what the toolbar sees a moment after Pause)
        var atLastStep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.BeforeRunnerReleased = async _ =>
        {
            atLastStep.TrySetResult();
#pragma warning disable VSTHRD003 // the test completes this source itself
            await release.Task;
#pragma warning restore VSTHRD003
        };
        var pause = harness.Engine.PauseJobAsync(job.Id, ct);
        await atLastStep.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Paused);
        harness.Engine.OptionsDecorator = null;

        var resume = harness.Engine.ResumeJobAsync(job.Id, ct);
        try
        {
            await Task.WhenAny(resume, Task.Delay(TimeSpan.FromSeconds(2), ct)); // a failing resume ends at once; a waiting one is still waiting
            resume.IsFaulted.Should().BeFalse("the pause is nearly over, so resume waits for it (it failed with 'already running')");
        }
        finally
        {
            release.TrySetResult(); // whatever happened above, the held pause must be let go or the engine cannot be disposed
        }

        await pause;
        await resume;

        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { State: JobState.Completed }, ct);
        await download;
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task Pause_waits_for_the_checkpoint_and_resume_continues_from_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/pause-resume/8388608", "pause-resume.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { State: JobState.Downloading, DoneBytes: > 0 }, ct);

        await harness.Engine.PauseJobAsync(job.Id, ct);
        await download;

        var paused = (await harness.Jobs.GetAsync(job.Id, ct))!;
        paused.State.Should().Be(JobState.Paused);
        paused.PauseReason.Should().Be(PauseReason.UserRequested);
        paused.DoneBytes.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(job.Folder, job.FileName + PartFileWriter.PartExtension)).Should().BeTrue();

        harness.Engine.OptionsDecorator = null;
        await harness.Engine.ResumeAndWaitAsync(job.Id, ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
        (await Sha256FileAsync(Path.Combine(job.Folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("pause-resume", 8 * 1024 * 1024));
    }

    [Fact]
    [Trait("TC", "TC-ENG-015")]
    public async Task Disk_full_pauses_with_NotEnoughSpace_and_resume_restarts_after_space_is_freed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/disk-full/2097152", "disk-full.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        var space = new NoSpaceProvider();
        harness.Engine.OptionsDecorator = (_, options) => options with { SpaceProvider = space };

        await harness.Engine.StartAndWaitAsync(job.Id, ct);

        var paused = (await harness.Jobs.GetAsync(job.Id, ct))!;
        paused.State.Should().Be(JobState.Paused);
        paused.PauseReason.Should().Be(PauseReason.NotEnoughSpace);

        space.Full = false;
        await harness.Engine.ResumeAndWaitAsync(job.Id, ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
        (await Sha256FileAsync(Path.Combine(job.Folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("disk-full", 2 * 1024 * 1024));
    }

    [Fact]
    [Trait("TC", "TC-RES-004")]
    public async Task Going_offline_pauses_with_NetworkOffline_and_reconnecting_resumes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var network = new FakeNetworkStatus();
        await using var harness = await Harness.CreateAsync(_host, _root, network: network);
        var job = harness.NewJob("/files/offline-resume/8388608", "offline-resume.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { State: JobState.Downloading, DoneBytes: > 0 }, ct);

        network.SetOnline(false);
        await download;
        var paused = (await harness.Jobs.GetAsync(job.Id, ct))!;
        paused.State.Should().Be(JobState.Paused);
        paused.PauseReason.Should().Be(PauseReason.NetworkOffline);

        harness.Engine.OptionsDecorator = null;
        network.SetOnline(true);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct))?.State == JobState.Completed, ct);

        (await Sha256FileAsync(Path.Combine(job.Folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("offline-resume", 8 * 1024 * 1024));
    }

    [Fact]
    [Trait("TC", "TC-RES-004")]
    public async Task Reconnecting_does_not_resume_a_job_the_user_paused()
    {
        var ct = TestContext.Current.CancellationToken;
        var network = new FakeNetworkStatus();
        await using var harness = await Harness.CreateAsync(_host, _root, network: network);
        var job = harness.NewJob("/files/user-pause/8388608", "user-pause.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { DoneBytes: > 0 }, ct);

        await harness.Engine.PauseJobAsync(job.Id, ct);
        await download;
        harness.Engine.OptionsDecorator = null;
        network.SetOnline(false);
        network.SetOnline(true);
        await Task.Delay(500, ct);

        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.State.Should().Be(JobState.Paused);
        stored.PauseReason.Should().Be(PauseReason.UserRequested);
    }

    [Fact]
    public async Task Cancel_stops_the_runner_and_removes_the_job_but_keeps_files_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/cancel-job/8388608", "cancel-job.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { DoneBytes: > 0 }, ct);

        await harness.Engine.CancelJobAsync(job.Id, cancellationToken: ct);
        await download;

        (await harness.Jobs.GetAsync(job.Id, ct)).Should().BeNull();
        File.Exists(Path.Combine(job.Folder, job.FileName + PartFileWriter.PartExtension)).Should().BeTrue();
    }

    [Fact]
    [Trait("TC", "TC-ENG-018")]
    public async Task A_refreshed_signed_link_continues_a_paused_job()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Current-user DPAPI requires Windows.");
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var job = harness.NewJob($"/signed/files/refresh/4194304?exp={expiry}", "refresh.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { State: JobState.Downloading, DoneBytes: > 0 }, ct);
        await harness.Engine.PauseJobAsync(job.Id, ct);
        await download;

        harness.Engine.OptionsDecorator = null;
        var fresh = new Uri(_host.BaseAddress, $"/signed/files/refresh/4194304?exp={expiry + 3600}");
        var outcome = await harness.Engine.RefreshLinkAsync(job.Id, fresh, cancellationToken: ct);

        outcome.Should().BeOfType<TransferCompleted>();
        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.State.Should().Be(JobState.Completed);
        stored.Url.Should().Be(fresh.AbsoluteUri);
        (await Sha256FileAsync(Path.Combine(job.Folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex("refresh", 4 * 1024 * 1024));
    }

    [Fact]
    [Trait("TC", "TC-RES-006")]
    public async Task A_changed_remote_file_restarts_the_job_and_keeps_no_mixed_content()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        const string id = "engine-mutable";
        const int size = 8 * 1024 * 1024;
        _host.Mutable.Change(id, size);
        var job = harness.NewJob($"/mutable/{id}", "engine-mutable.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        var restarts = new ConcurrentQueue<JobRestarted>();
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobRestarted restarted && restarted.Id == job.Id)
            {
                restarts.Enqueue(restarted);
            }
        };
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { DoneBytes: > 0 }, ct);
        _host.Mutable.Change(id);

        await download;

        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.State.Should().Be(JobState.Completed);
        stored.RestartCount.Should().Be(1);
        restarts.Should().ContainSingle();
        (await Sha256FileAsync(Path.Combine(job.Folder, job.FileName), ct))
            .Should().Be(GeneratedContent.Sha256Hex($"{id}.v2", size));
    }

    [Fact]
    [Trait("TC", "TC-RES-006")]
    public async Task The_third_changed_file_fails_the_job_with_the_stored_restart_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        const string id = "engine-mutable-cap";
        // a change is only seen by the next request, so the file has to outlast the three changes by a wide margin on a slow runner
        const int size = 32 * 1024 * 1024;
        _host.Mutable.Change(id, size);
        var job = harness.NewJob($"/mutable/{id}", "engine-mutable-cap.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await EventuallyAsync(async () =>
            {
                var stored = await harness.Jobs.GetAsync(job.Id, ct);
                return stored is { State: JobState.Downloading, DoneBytes: > 0, RestartCount: var count } && count == attempt;
            }, ct);
            _host.Mutable.Change(id);
        }

        await download;

        var failed = (await harness.Jobs.GetAsync(job.Id, ct))!;
        failed.State.Should().Be(JobState.Failed, $"the job ended with {failed.DoneBytes} of {size} bytes done and {failed.RestartCount} restart(s)");
        failed.Error.Should().Contain("changed repeatedly");
        failed.RestartCount.Should().Be(2);
    }

    [Fact]
    public async Task Shutdown_pauses_a_running_job_with_its_checkpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/shutdown-pause/8388608", "shutdown-pause.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var download = harness.Engine.StartAndWaitAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { DoneBytes: > 0 }, ct);

        await harness.Engine.StopAsync(ct);
        await download;

        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.State.Should().Be(JobState.Paused);
        stored.PauseReason.Should().Be(PauseReason.UserRequested);
    }

    [Fact]
    [Trait("TC", "TC-RES-002")]
    public async Task Recovery_pauses_a_job_with_a_valid_part_and_reconciles_its_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var job = harness.NewJob("/files/recover-part/1048576", "recover-part.bin") with
        {
            State = JobState.Downloading,
            TotalBytes = 1024 * 1024,
            Resumable = true,
        };
        await harness.Jobs.AddAsync(job, ct);
        await harness.Segments.ReplaceAsync(job.Id, [new Segment(0, 0, 1024 * 1024, 4096)], ct);
        await File.WriteAllBytesAsync(Path.Combine(job.Folder, job.FileName + PartFileWriter.PartExtension), new byte[1024 * 1024], ct);
        var states = new ConcurrentQueue<JobState>();
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobStateChanged changed && changed.Id == job.Id)
            {
                states.Enqueue(changed.To);
            }
        };

        await harness.Engine.StartAsync(ct);

        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.State.Should().Be(JobState.Paused);
        stored.PauseReason.Should().Be(PauseReason.CrashRecovery);
        stored.DoneBytes.Should().Be(4096);
        // events reach subscribers on the dispatcher thread, a moment after the transition committed
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!states.Contains(JobState.Paused) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, ct);
        }

        states.Should().Contain(JobState.Paused);
    }

    [Fact]
    public async Task Recovery_completes_a_job_whose_final_file_is_already_present()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var job = harness.NewJob("/files/recover-final/4", "recover-final.bin") with
        {
            State = JobState.Downloading,
            TotalBytes = 4,
            DoneBytes = 4,
        };
        await harness.Jobs.AddAsync(job, ct);
        await File.WriteAllBytesAsync(Path.Combine(job.Folder, job.FileName), [1, 2, 3, 4], ct);

        await harness.Engine.StartAsync(ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
    }

    [Fact]
    public async Task Recovery_finalises_a_complete_part_left_in_Verifying()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var job = harness.NewJob("/files/recover-verifying/4", "recover-verifying.bin") with
        {
            State = JobState.Verifying,
            TotalBytes = 4,
            DoneBytes = 4,
            ETag = "\"verifying\"",
        };
        await harness.Jobs.AddAsync(job, ct);
        await harness.Segments.ReplaceAsync(job.Id, [new Segment(0, 0, 4, 4)], ct);
        await File.WriteAllBytesAsync(Path.Combine(job.Folder, job.FileName + PartFileWriter.PartExtension), [9, 8, 7, 6], ct);

        await harness.Engine.StartAsync(ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
        (await File.ReadAllBytesAsync(Path.Combine(job.Folder, job.FileName), ct)).Should().Equal(9, 8, 7, 6);
    }

    [Fact]
    public async Task Recovery_requeues_a_job_that_was_still_probing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var job = harness.NewJob("/files/recover-probing/1048576", "recover-probing.bin") with { State = JobState.Probing };
        await harness.Jobs.AddAsync(job, ct);

        await harness.Engine.StartAsync(ct);

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Queued);
    }

    [Fact]
    public async Task Start_refuses_a_database_that_fails_quick_check()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false);
        var failing = new FailingQuickCheckJobs(harness.Jobs);
        await using var engine = new DownloadEngine(failing, harness.Segments, harness.Writer, new BandwidthLimiter(), timeProvider: TimeProvider.System);

        Func<Task> start = () => engine.StartAsync(ct);

        await start.Should().ThrowAsync<InvalidOperationException>();
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the condition should become true within 20 s");
            await Task.Delay(20, cancellationToken);
        }
    }

    [Fact]
    public async Task Start_returns_while_the_job_runs_and_a_caller_token_does_not_govern_the_run()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/start-returns/8388608", "start-returns.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        using var shortLived = new CancellationTokenSource();

        await harness.Engine.StartJobAsync(job.Id, shortLived.Token);
        await shortLived.CancelAsync(); // the call is over; this must not pause the job

        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().NotBe(JobState.Completed, "Start returned long before the slow download ended");
        harness.Engine.OptionsDecorator = null;
        await harness.Engine.PauseJobAsync(job.Id, ct);
        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Paused);
        (await harness.Jobs.GetAsync(job.Id, ct))!.PauseReason.Should().Be(PauseReason.UserRequested);
        await harness.Engine.ResumeAndWaitAsync(job.Id, ct);
        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Completed);
    }

    [Fact]
    public async Task A_new_job_must_be_queued_with_no_progress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/bad-add/1048576", "bad-add.bin");

        var downloading = async () => await harness.Engine.AddJobAsync(job with { State = JobState.Downloading }, cancellationToken: ct);
        var progressed = async () => await harness.Engine.AddJobAsync(job with { DoneBytes = 10 }, cancellationToken: ct);

        await downloading.Should().ThrowAsync<ArgumentException>();
        await progressed.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task An_authentication_failure_pauses_with_its_own_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/auth/basic/files/needs-login/1048576", "needs-login.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);

        await harness.Engine.StartAndWaitAsync(job.Id, ct);

        var stored = (await harness.Jobs.GetAsync(job.Id, ct))!;
        stored.State.Should().Be(JobState.Paused);
        stored.PauseReason.Should().Be(PauseReason.AuthRequired);
    }

    [Fact]
    public async Task A_running_job_reports_progress_and_enters_verifying_before_it_completes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        const int size = 3 * 1024 * 1024;
        var job = harness.NewJob($"/files/progress-verify/{size}", "progress-verify.bin") with
        {
            ChecksumAlgo = "sha256",
            Checksum = GeneratedContent.Sha256Hex("progress-verify", size),
        };
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        JobState? stateWhenHashing = null;
        harness.Engine.OptionsDecorator = (_, options) => options with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = harness.Host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 }) },
            BeforeVerifyAsync = async () =>
            {
                await options.BeforeVerifyAsync!();
                stateWhenHashing = (await harness.Jobs.GetAsync(job.Id, ct))!.State;
            },
        };
        var events = new ConcurrentQueue<EngineEvent>();
        harness.Engine.EventRaised += events.Enqueue;

        await harness.Engine.StartAndWaitAsync(job.Id, ct);
        await Task.Delay(200, ct); // the dispatcher is asynchronous

        events.OfType<JobProgressed>().Count(p => p.Id == job.Id && p.BytesPerSecond > 0).Should().BeGreaterThan(0, "a running job publishes speed samples");
        var order = events.OfType<JobStateChanged>().Where(e => e.Id == job.Id).Select(e => e.To).ToList();
        order.Should().ContainInOrder(JobState.Downloading, JobState.Verifying, JobState.Completed);
        stateWhenHashing.Should().Be(JobState.Verifying, "the job shows Verifying while the checksum is computed, not after the commit");
    }

    [Fact]
    [Trait("TC", "TC-UI-002")]
    public async Task A_running_job_publishes_its_segments_and_connections_while_it_transfers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        const int Size = 3 * 1024 * 1024;
        var job = harness.NewJob($"/files/shape/{Size}", "shape.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = harness.Host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 }) },
        };
        var shapes = new ConcurrentQueue<SegmentsProgressed>();
        harness.Engine.EventRaised += e =>
        {
            if (e is SegmentsProgressed shape && shape.Id == job.Id)
            {
                shapes.Enqueue(shape);
            }
        };

        await harness.Engine.StartAndWaitAsync(job.Id, ct);
        await Task.Delay(200, ct); // the dispatcher is asynchronous

        var all = shapes.ToList();
        all.Count.Should().BeGreaterThan(2, "the shape is published several times while a multi-second transfer runs, not once per second");
        foreach (var shape in all)
        {
            shape.Segments.Sum(x => x.Length).Should().Be(Size, "the segments always cover the whole file");
            shape.Segments.Should().OnlyContain(x => x.Written >= 0 && x.Written <= x.Length);
            shape.Connections.Count.Should().BeLessThanOrEqualTo(4);
            shape.Connections.Should().OnlyContain(c => c.Start >= 0 && c.End <= Size && c.BytesPerSecond >= 0);
        }

        all.Select(x => x.Segments.Sum(y => y.Written)).Should().BeInAscendingOrder("bytes only move forward");
        all.Should().Contain(x => x.Connections.Count > 1, "four connections were allowed");
        all.SelectMany(x => x.Connections).Should().Contain(c => c.BytesPerSecond > 0, "a moving connection reports a speed");
    }

    [Fact]
    public async Task The_job_log_tells_the_probe_result_and_the_connection_count_without_an_address()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        const int Size = 2 * 1024 * 1024;
        var job = harness.NewJob($"/files/log/{Size}", "log.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        harness.Engine.OptionsDecorator = (_, options) => options with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = harness.Host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 }) },
        };
        var lines = new ConcurrentQueue<string>();
        harness.Engine.EventRaised += e =>
        {
            if (e is JobLogged logged && logged.Id == job.Id)
            {
                lines.Enqueue(logged.Line);
            }
        };

        await harness.Engine.StartAndWaitAsync(job.Id, ct);
        await Task.Delay(200, ct);

        var log = lines.ToList();
        log.Should().Contain(l => l.StartsWith("Probe: ranges supported, 2097152 bytes", StringComparison.Ordinal));
        log.Should().Contain(l => l.StartsWith("Connections: ", StringComparison.Ordinal));
        log.Should().OnlyContain(l => !l.Contains("http", StringComparison.OrdinalIgnoreCase) && !l.Contains(harness.Folder, StringComparison.OrdinalIgnoreCase) && !l.Contains("127.0.0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_engine_refuses_a_transition_the_state_machine_does_not_allow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/illegal-edge/1048576", "illegal-edge.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);

        var act = async () => await harness.Engine.TransitionAsync(job.Id, JobState.Queued, JobState.Completed, cancellationToken: ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().Be(JobState.Queued);
    }

    [Fact]
    public async Task The_first_speed_sample_after_a_resume_does_not_count_the_bytes_saved_before()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root);
        var job = harness.NewJob("/files/resume-speed/6291456", "resume-speed.bin");
        await harness.Engine.AddJobAsync(job, cancellationToken: ct);
        var chaos = new Dictionary<string, string>
        {
            [ChaosRegistry.HeaderName] = harness.Host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 128 * 1024 }),
        };
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = chaos };
        await harness.Engine.StartJobAsync(job.Id, ct);
        await EventuallyAsync(async () => (await harness.Jobs.GetAsync(job.Id, ct)) is { DoneBytes: >= 1_500_000 }, ct);
        await harness.Engine.PauseJobAsync(job.Id, ct);
        var samples = new ConcurrentQueue<JobProgressed>();
        harness.Engine.EventRaised += engineEvent =>
        {
            if (engineEvent is JobProgressed progressed && progressed.Id == job.Id)
            {
                samples.Enqueue(progressed);
            }
        };

        await harness.Engine.ResumeJobAsync(job.Id, ct);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (samples.Count < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, ct);
        }

        await harness.Engine.PauseJobAsync(job.Id, ct);
        samples.Should().NotBeEmpty();
        // four connections at 128 KiB/s cannot exceed about 0.5 MiB/s; counting the 1.5 MiB saved earlier would show 1.5 MiB/s or more
        samples.Select(sample => sample.BytesPerSecond).Should().OnlyContain(speed => speed < 1_000_000);
    }

    [Fact]
    public async Task Start_up_does_not_wait_for_a_network_paused_job_to_finish()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await Harness.CreateAsync(_host, _root, startEngine: false, network: new FakeNetworkStatus());
        var job = harness.NewJob("/files/startup-resume/8388608", "startup-resume.bin") with
        {
            State = JobState.Paused,
            PauseReason = PauseReason.NetworkOffline,
        };
        await harness.Jobs.AddAsync(job, ct);
        harness.Engine.OptionsDecorator = (_, options) => options with { RequestHeaders = harness.AddSlowChaos() };
        var started = System.Diagnostics.Stopwatch.StartNew();

        await harness.Engine.StartAsync(ct);

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "StartAsync resumes the job in the background");
        (await harness.Jobs.GetAsync(job.Id, ct))!.State.Should().NotBe(JobState.Completed);
        harness.Engine.OptionsDecorator = null;
        await harness.Engine.PauseJobAsync(job.Id, ct);
    }

    private sealed class FakeNetworkStatus : INetworkStatus
    {
        public bool IsOnline { get; private set; } = true;

        public event EventHandler? OnlineChanged;

        public void SetOnline(bool online)
        {
            if (IsOnline == online)
            {
                return;
            }

            IsOnline = online;
            OnlineChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class NoSpaceProvider : IDiskSpaceProvider
    {
        public bool Full { get; set; } = true;

        public bool TryGetAvailableFreeBytes(string directoryPath, out long availableBytes)
        {
            availableBytes = Full ? 0 : long.MaxValue / 2;
            return true;
        }
    }

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var hasher = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string folder, TestServerHost host, Uri baseAddress, JobRepository jobs, SegmentRepository segments, JobSecretRepository secrets, CheckpointWriter writer, DownloadEngine engine)
        {
            Folder = folder;
            Host = host;
            BaseAddress = baseAddress;
            Jobs = jobs;
            Segments = segments;
            Secrets = secrets;
            Writer = writer;
            Engine = engine;
        }

        public string Folder { get; }

        public TestServerHost Host { get; }

        public Uri BaseAddress { get; }

        public JobSecretRepository Secrets { get; }

        public JobRepository Jobs { get; }

        public SegmentRepository Segments { get; }

        public CheckpointWriter Writer { get; }

        public DownloadEngine Engine { get; }

        public static async Task<Harness> CreateAsync(TestServerHost host, string root, bool startEngine = true, INetworkStatus? network = null)
        {
            var folder = Path.Combine(root, "downloads-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var factory = new SqliteConnectionFactory(Path.Combine(folder, "engine.db"));
            await new MigrationRunner(factory).MigrateAsync(TestContext.Current.CancellationToken);
            var jobs = new JobRepository(factory);
            var segments = new SegmentRepository(factory);
#pragma warning disable CA1416 // current-user DPAPI; the test runs on Windows or skips
            var secrets = new JobSecretRepository(factory);
#pragma warning restore CA1416
#pragma warning disable CA2000 // Harness owns the writer and engine and disposes both
            var writer = new CheckpointWriter(factory);
            writer.Start();
            var engine = new DownloadEngine(jobs, segments, writer, new BandwidthLimiter(), secrets: secrets, timeProvider: TimeProvider.System, network: network);
#pragma warning restore CA2000
            if (startEngine)
            {
                await engine.StartAsync(TestContext.Current.CancellationToken);
            }

            return new Harness(folder, host, host.BaseAddress, jobs, segments, secrets, writer, engine);
        }

        public Dictionary<string, string> AddSlowChaos()
        {
            var id = Host.Chaos.Register(new ChaosProfile { ThrottleBytesPerSecond = 512 * 1024 });
            return new Dictionary<string, string> { [ChaosRegistry.HeaderName] = id };
        }

        public Job NewJob(string path, string fileName) => new()
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = new Uri(BaseAddress, path).AbsoluteUri,
            FileName = fileName,
            Folder = Folder,
            QueueId = 1,
            QueuePos = 1,
            CreatedUtc = TimeProvider.System.GetUtcNow(),
            Connections = 4,
            TotalBytes = null,
        };

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            await Writer.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(Folder))
                {
                    Directory.Delete(Folder, recursive: true);
                }
            }
            catch (IOException)
            {
                // The test's root cleanup will retry once the pool has released the file.
            }
        }
    }

    private sealed class FailingQuickCheckJobs(IJobRepository inner) : IJobRepository
    {
        public Task AddAsync(Job job, CancellationToken cancellationToken = default) => inner.AddAsync(job, cancellationToken);

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<Job>> ListAsync(int? queueId = null, JobState? state = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(queueId, state, cancellationToken);

        public Task<bool> UpdateAsync(Job job, CancellationToken cancellationToken = default) => inner.UpdateAsync(job, cancellationToken);

        public Task<bool> UpdateTransferMetadataAsync(JobId id, JobTransferMetadata metadata, CancellationToken cancellationToken = default) =>
            inner.UpdateTransferMetadataAsync(id, metadata, cancellationToken);

        public Task<bool> UpdateUrlAsync(JobId id, string url, string? finalUrl, CancellationToken cancellationToken = default) =>
            inner.UpdateUrlAsync(id, url, finalUrl, cancellationToken);

        public Task<bool> UpdateQueuePositionAsync(JobId id, int queueId, double queuePos, CancellationToken cancellationToken = default) =>
            inner.UpdateQueuePositionAsync(id, queueId, queuePos, cancellationToken);

        public Task<bool> ResetProgressAsync(JobId id, CancellationToken cancellationToken = default) => inner.ResetProgressAsync(id, cancellationToken);

        public Task<bool> DeleteAsync(JobId id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);

        public Task<bool> TryChangeStateAsync(JobId id, JobState fromState, JobState toState, string? failureMessage = null, PauseReason? pauseReason = null, CancellationToken cancellationToken = default) =>
            inner.TryChangeStateAsync(id, fromState, toState, failureMessage, pauseReason, cancellationToken);

        public Task<bool> ReconcileProgressAsync(JobId id, long doneBytes, CancellationToken cancellationToken = default) =>
            inner.ReconcileProgressAsync(id, doneBytes, cancellationToken);

        public Task<int?> IncrementRestartCountAsync(JobId id, CancellationToken cancellationToken = default) =>
            inner.IncrementRestartCountAsync(id, cancellationToken);

        public Task<bool> QuickCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
