using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Core;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

internal sealed class NoRefresher : ILinkRefresher
{
    public Task<RefreshOutcome> RefreshAsync(JobId id, CancellationToken cancellationToken) => Task.FromResult(RefreshOutcome.Failed);
}

internal sealed class ScriptedRefresher(RefreshOutcome outcome) : ILinkRefresher
{
    public TaskCompletionSource? Gate { get; set; }

    public List<JobId> Asked { get; } = [];

    public async Task<RefreshOutcome> RefreshAsync(JobId id, CancellationToken cancellationToken)
    {
        Asked.Add(id);
        if (Gate is not null)
        {
#pragma warning disable VSTHRD003 // the test releases it
            await Gate.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
        }

        return outcome;
    }
}

[Trait("Requirement", "KASI-ENG-010")]
public class RefreshBrokerTests
{
    private sealed class FakeChannel(int reached) : IRefreshChannel
    {
        public List<RefreshLink> Sent { get; } = [];

        public Task<int> SendAsync(RefreshLink message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.FromResult(reached);
        }
    }

    private static LinkRefreshed Answer(string jobId, string url = "https://files.example.org/fresh.zip?token=NEW") => new()
    {
        Id = "x1",
        JobId = jobId,
        Url = url,
        Cookies = [new IpcCookie { Name = "sid", Value = "COOKIE-VALUE", Domain = ".example.org", Path = "/", Secure = true, HttpOnly = true }],
    };

    [Fact]
    public async Task A_request_goes_to_the_browsers_and_the_matching_answer_ends_the_wait()
    {
        var channel = new FakeChannel(1);
        var broker = new RefreshBroker(new FakeTimeProvider());
        broker.Bind(channel);

        var request = broker.RequestAsync("job-1", "https://files.example.org/page", "fresh.zip", TestContext.Current.CancellationToken);
        broker.TryComplete(Answer("job-1")).Should().BeTrue();
        var result = await request;

        channel.Sent.Should().ContainSingle().Which.Should().Match<RefreshLink>(m => m.JobId == "job-1" && m.ReferrerUrl == "https://files.example.org/page" && m.MatchHint == "fresh.zip");
        result.Status.Should().Be(RefreshStatus.Refreshed);
        result.Answer!.Cookies.Should().ContainSingle();
        result.Answer.ToString().Should().Be("RefreshAnswer");
    }

    [Fact]
    public async Task With_no_browser_connected_nothing_waits()
    {
        var broker = new RefreshBroker(new FakeTimeProvider());
        broker.Bind(new FakeChannel(0));

        var result = await broker.RequestAsync("job-1", "https://files.example.org/page", null, TestContext.Current.CancellationToken);

        result.Status.Should().Be(RefreshStatus.NoBrowser);
        broker.TryComplete(Answer("job-1")).Should().BeFalse("the request is gone");
    }

    [Fact]
    public async Task Before_the_pipe_exists_every_request_is_no_browser()
    {
        var result = await new RefreshBroker().RequestAsync("job-1", "https://files.example.org/page", null, TestContext.Current.CancellationToken);

        result.Status.Should().Be(RefreshStatus.NoBrowser);
    }

    [Fact]
    public async Task No_answer_in_time_is_a_timeout_and_a_late_answer_is_refused()
    {
        var clock = new FakeTimeProvider();
        var broker = new RefreshBroker(clock, TimeSpan.FromSeconds(70));
        broker.Bind(new FakeChannel(1));

        var request = broker.RequestAsync("job-1", "https://files.example.org/page", null, TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(71));
        var result = await request;

        result.Status.Should().Be(RefreshStatus.TimedOut);
        broker.TryComplete(Answer("job-1")).Should().BeFalse();
    }

    [Fact]
    public async Task An_answer_nobody_asked_for_and_a_second_answer_are_refused()
    {
        var broker = new RefreshBroker(new FakeTimeProvider());
        broker.Bind(new FakeChannel(2));
        var request = broker.RequestAsync("job-1", "https://files.example.org/page", null, TestContext.Current.CancellationToken);

        broker.TryComplete(Answer("other-job")).Should().BeFalse();
        broker.TryComplete(Answer("job-1")).Should().BeTrue();
        broker.TryComplete(Answer("job-1")).Should().BeFalse("the first browser to answer wins");
        (await request).Status.Should().Be(RefreshStatus.Refreshed);
    }

    [Fact]
    public async Task Cancelling_the_wait_throws_and_forgets_the_request()
    {
        var broker = new RefreshBroker(new FakeTimeProvider());
        broker.Bind(new FakeChannel(1));
        using var cancel = new CancellationTokenSource();

        var request = broker.RequestAsync("job-1", "https://files.example.org/page", null, cancel.Token);
        await cancel.CancelAsync();

#pragma warning disable VSTHRD003 // started above in this test
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
#pragma warning restore VSTHRD003
        broker.TryComplete(Answer("job-1")).Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Over_a_real_pipe_the_extension_receives_refreshLink_and_its_linkRefreshed_completes_the_request()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = TestBrowser.New();
        var endpoints = TestEndpoints.New();
        await using var server = new KasiPipeServer(endpoints.BrowserPipe, services.Handler.Handler);
        server.Start();
        services.Refreshes.Bind(new PipeRefreshChannel(server));
        await using var extension = await KasiPipeClient.ConnectAsync(endpoints.BrowserPipe, TimeSpan.FromSeconds(10), ct);
        await extension.SendAsync(new Hello { Id = "h1", Client = BrowserClient.Chrome, ExtensionVersion = "0.2.0", ProtocolMin = 1, ProtocolMax = 1 }, ct);
        (await extension.ReceiveAsync(ct)).Should().BeOfType<Rules>();

        var request = services.Refreshes.RequestAsync("job-1", "https://files.example.org/page", "fresh.zip", ct);
        var received = (await extension.ReceiveAsync(ct)).Should().BeOfType<RefreshLink>().Subject;
        await extension.SendAsync(Answer(received.JobId), ct);
        var reply = await extension.ReceiveAsync(ct);

        reply.Should().BeOfType<Ack>().Which.Id.Should().Be("x1");
        (await request).Status.Should().Be(RefreshStatus.Refreshed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task A_linkRefreshed_nobody_asked_for_is_refused_over_the_pipe()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = TestBrowser.New();
        var endpoints = TestEndpoints.New();
        await using var server = new KasiPipeServer(endpoints.BrowserPipe, services.Handler.Handler);
        server.Start();
        await using var extension = await KasiPipeClient.ConnectAsync(endpoints.BrowserPipe, TimeSpan.FromSeconds(10), ct);
        await extension.SendAsync(new Hello { Id = "h1", Client = BrowserClient.Chrome, ExtensionVersion = "0.2.0", ProtocolMin = 1, ProtocolMax = 1 }, ct);
        await extension.ReceiveAsync(ct);

        await extension.SendAsync(Answer("job-1"), ct);

        (await extension.ReceiveAsync(ct)).Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.InvalidMessage);
    }
}

[Trait("Requirement", "KASI-ENG-010")]
public class LinkRefresherTests
{
    private sealed class FakeBroker(RefreshResult result) : IRefreshBroker
    {
        public List<(string JobId, string Referrer, string? Hint)> Asked { get; } = [];

        public Task<RefreshResult> RequestAsync(string jobId, string referrerUrl, string? matchHint, CancellationToken cancellationToken)
        {
            Asked.Add((jobId, referrerUrl, matchHint));
            return Task.FromResult(result);
        }

        public bool TryComplete(LinkRefreshed message) => false;
    }

    private static Job PausedJob(string? referrer = "https://files.example.org/page") => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = JobKind.Http,
        Url = "https://files.example.org/old.zip?token=OLD",
        Referrer = referrer,
        FileName = "pack.zip",
        Folder = @"C:\Downloads",
        QueueId = 1,
        QueuePos = 1,
        State = JobState.Paused,
        PauseReason = PauseReason.AuthRequired,
        CreatedUtc = DateTimeOffset.UnixEpoch,
    };

    private static RefreshResult Found() => new(RefreshStatus.Refreshed, new RefreshAnswer("https://files.example.org/new.zip?token=NEW", [new IpcCookie { Name = "sid", Value = "COOKIE-VALUE", Domain = ".example.org", Path = "/", Secure = true, HttpOnly = true }]));

    [Fact]
    public async Task The_page_is_asked_for_with_the_file_name_as_hint_and_the_new_link_and_cookies_reach_the_engine()
    {
        var jobs = new RecordingJobService();
        var job = PausedJob();
        jobs.Stored[job.Id] = job;
        var broker = new FakeBroker(Found());

        var outcome = await new LinkRefresher(jobs, broker).RefreshAsync(job.Id, TestContext.Current.CancellationToken);

        outcome.Should().Be(RefreshOutcome.Refreshed);
        broker.Asked.Should().ContainSingle().Which.Should().Be((job.Id.ToString(), "https://files.example.org/page", "pack.zip"));
        var refreshed = jobs.Refreshes.Should().ContainSingle().Subject;
        refreshed.Url.Should().Be("https://files.example.org/new.zip?token=NEW");
        refreshed.Cookies.Should().ContainSingle(c => c.Name == "sid" && c.Value == "COOKIE-VALUE" && c.Secure && c.HttpOnly);
    }

    [Fact]
    public async Task A_job_that_does_not_exist_or_has_no_referrer_never_reaches_the_browser()
    {
        var jobs = new RecordingJobService();
        var typed = PausedJob(referrer: null);
        jobs.Stored[typed.Id] = typed;
        var broker = new FakeBroker(Found());
        var refresher = new LinkRefresher(jobs, broker);

        (await refresher.RefreshAsync(JobId.New(TimeProvider.System), TestContext.Current.CancellationToken)).Should().Be(RefreshOutcome.NotFound);
        (await refresher.RefreshAsync(typed.Id, TestContext.Current.CancellationToken)).Should().Be(RefreshOutcome.NoReferrer);

        broker.Asked.Should().BeEmpty();
    }

    [Theory]
    [InlineData((int)RefreshStatus.NoBrowser, (int)RefreshOutcome.NoBrowser)]
    [InlineData((int)RefreshStatus.TimedOut, (int)RefreshOutcome.TimedOut)]
    public async Task No_answer_from_the_browser_is_reported_and_the_engine_is_left_alone(int statusValue, int expectedValue)
    {
        var status = (RefreshStatus)statusValue;
        var expected = (RefreshOutcome)expectedValue;
        var jobs = new RecordingJobService();
        var job = PausedJob();
        jobs.Stored[job.Id] = job;

        var outcome = await new LinkRefresher(jobs, new FakeBroker(new RefreshResult(status))).RefreshAsync(job.Id, TestContext.Current.CancellationToken);

        outcome.Should().Be(expected);
        jobs.Refreshes.Should().BeEmpty();
    }

    [Theory]
    [InlineData((int)RefreshProblem.Rejected, (int)RefreshOutcome.Rejected)]
    [InlineData((int)RefreshProblem.NotResumable, (int)RefreshOutcome.Failed)]
    [InlineData((int)RefreshProblem.Failed, (int)RefreshOutcome.Failed)]
    public async Task The_engines_refusal_is_reported(int problemValue, int expectedValue)
    {
        var expected = (RefreshOutcome)expectedValue;
        var jobs = new RecordingJobService { RefreshProblem = (RefreshProblem)problemValue };
        var job = PausedJob();
        jobs.Stored[job.Id] = job;

        var outcome = await new LinkRefresher(jobs, new FakeBroker(Found())).RefreshAsync(job.Id, TestContext.Current.CancellationToken);

        outcome.Should().Be(expected);
    }
}

[Trait("Requirement", "KASI-ENG-010")]
public class RefreshLinkDialogTests
{
    private static RefreshLinkViewModel Dialog(ILinkRefresher refresher) => new(JobId.New(TimeProvider.System), "pack.zip", refresher);

    [Fact]
    public void It_names_the_file_and_not_the_address_and_offers_the_try()
    {
        using var dialog = Dialog(new ScriptedRefresher(RefreshOutcome.Refreshed));

        dialog.StatusText.Should().Contain("pack.zip").And.NotContain("http");
        dialog.Phase.Should().Be(RefreshPhase.Ask);
        dialog.StartCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_good_refresh_says_so_and_cannot_be_started_again()
    {
        var refresher = new ScriptedRefresher(RefreshOutcome.Refreshed);
        using var dialog = Dialog(refresher);

        await dialog.StartCommand.ExecuteAsync(null);

        dialog.Phase.Should().Be(RefreshPhase.Done);
        dialog.Outcome.Should().Be(RefreshOutcome.Refreshed);
        dialog.StatusText.Should().Contain("continues");
        dialog.StartCommand.CanExecute(null).Should().BeFalse();
        refresher.Asked.Should().ContainSingle();
    }

    [Theory]
    [InlineData((int)RefreshOutcome.NoBrowser, "No browser")]
    [InlineData((int)RefreshOutcome.TimedOut, "did not start a download")]
    [InlineData((int)RefreshOutcome.NoReferrer, "does not know which page")]
    [InlineData((int)RefreshOutcome.Rejected, "different file")]
    [InlineData((int)RefreshOutcome.Failed, "could not be refreshed")]
    public async Task A_failure_says_why_and_allows_another_try(int outcomeValue, string words)
    {
        using var dialog = Dialog(new ScriptedRefresher((RefreshOutcome)outcomeValue));

        await dialog.StartCommand.ExecuteAsync(null);

        dialog.Phase.Should().Be(RefreshPhase.Failed);
        dialog.StatusText.Should().Contain(words);
        dialog.StartCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task While_waiting_the_button_is_off_and_cancel_stops_the_wait_and_closes()
    {
        var refresher = new ScriptedRefresher(RefreshOutcome.Refreshed) { Gate = new TaskCompletionSource() };
        using var dialog = Dialog(refresher);
        var closed = 0;
        dialog.Closed += () => closed++;

        var running = dialog.StartCommand.ExecuteAsync(null);
        dialog.Phase.Should().Be(RefreshPhase.Waiting);
        dialog.StartCommand.CanExecute(null).Should().BeFalse();
        dialog.CancelCommand.Execute(null);
        await running;

        closed.Should().Be(1);
        dialog.Outcome.Should().BeNull();
    }

    [AvaloniaFact]
    public void The_window_has_named_buttons_and_the_status()
    {
        var window = new RefreshLinkWindow { DataContext = Dialog(new ScriptedRefresher(RefreshOutcome.Refreshed)) };

        window.Show();

        AutomationProperties.GetName(window).Should().Be("Refresh link window");
        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        buttons.Should().HaveCountGreaterThanOrEqualTo(2);
        buttons.Should().OnlyContain(b => !string.IsNullOrEmpty(AutomationProperties.GetName(b)));
    }
}

[Trait("Requirement", "KASI-ENG-010")]
public class RefreshTriggerTests
{
    private sealed class Host : IAddDialogHost
    {
        public List<RefreshLinkViewModel> Refreshes { get; } = [];

        public Task ShowAddAsync(AddDownloadViewModel dialog) => Task.CompletedTask;

        public Task ShowPickerAsync(LinkPickerViewModel picker) => Task.CompletedTask;

        public Task ShowRefreshAsync(RefreshLinkViewModel dialog)
        {
            lock (Refreshes)
            {
                Refreshes.Add(dialog);
            }

            return Task.CompletedTask;
        }

        public Task<Kasi.Engine.DuplicateAction?> AskDuplicateAsync(Kasi.Engine.DuplicateCheckResult check) => Task.FromResult<Kasi.Engine.DuplicateAction?>(null);
    }

    private sealed class EventService : IJobService
    {
        private readonly RecordingJobService _inner = new();

        public event Action<EngineEvent>? EventRaised;

        public Dictionary<JobId, Job> Stored => _inner.Stored;

        public void Raise(EngineEvent e) => EventRaised?.Invoke(e);

        public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) => _inner.ListAsync(cancellationToken);

        public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default) => _inner.CategoriesAsync(cancellationToken);

        public Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default) => _inner.QueuesAsync(cancellationToken);

        public Task<AddResult> AddAsync(NewDownload request, Kasi.Engine.DuplicateAction? resolution = null, CancellationToken cancellationToken = default) => _inner.AddAsync(request, resolution, cancellationToken);

        public Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken = default) => _inner.GetAsync(id, cancellationToken);

        public Task<LinkRefreshResult> RefreshLinkAsync(JobId id, string url, IReadOnlyList<JobCookie> cookies, CancellationToken cancellationToken = default) => _inner.RefreshLinkAsync(id, url, cookies, cancellationToken);

        public long? SpeedLimit
        {
            get => _inner.SpeedLimit;
            set => _inner.SpeedLimit = value;
        }

        public Task PauseAllAsync(CancellationToken cancellationToken = default) => _inner.PauseAllAsync(cancellationToken);

        public Task ResumeAllAsync(CancellationToken cancellationToken = default) => _inner.ResumeAllAsync(cancellationToken);

        public Task StartAsync(JobId id, CancellationToken cancellationToken = default) => _inner.StartAsync(id, cancellationToken);

        public Task PauseAsync(JobId id, CancellationToken cancellationToken = default) => _inner.PauseAsync(id, cancellationToken);

        public Task ResumeAsync(JobId id, CancellationToken cancellationToken = default) => _inner.ResumeAsync(id, cancellationToken);

        public Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default) => _inner.CancelAsync(id, deleteFiles, cancellationToken);
    }

    private sealed class Fixed(BrowserSettings current) : IBrowserSettings
    {
        public BrowserSettings Current { get; } = current;

#pragma warning disable CS0067
        public event Action<BrowserSettings>? Changed;
#pragma warning restore CS0067

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static Job Paused(PauseReason? reason, string? referrer = "https://files.example.org/page", JobKind kind = JobKind.Http) => new()
    {
        Id = JobId.New(TimeProvider.System),
        Kind = kind,
        Url = "https://files.example.org/a.zip",
        Referrer = referrer,
        FileName = "a.zip",
        Folder = @"C:\Downloads",
        QueueId = 1,
        QueuePos = 1,
        State = JobState.Paused,
        PauseReason = reason,
        CreatedUtc = DateTimeOffset.UnixEpoch,
    };

    private static (EventService Jobs, Host Host, AddCoordinator Coordinator) Start()
    {
        var jobs = new EventService();
        var host = new Host();
        var coordinator = new AddCoordinator(jobs, new MemorySites(), new Fixed(new BrowserSettings()), host, new NoRefresher());
        coordinator.Watch();
        return (jobs, host, coordinator);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(30)); // generous: the coordinator works on the thread pool
        while (!condition())
        {
            await Task.Delay(10, limit.Token);
        }
    }

    [Fact]
    public async Task A_download_paused_for_want_of_access_opens_the_dialog_once()
    {
        var (jobs, host, _) = Start();
        var job = Paused(PauseReason.AuthRequired);
        jobs.Stored[job.Id] = job;

        jobs.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Paused, null));
        await WaitUntilAsync(() => host.Refreshes.Count == 1);
        jobs.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Paused, null));
        await Task.Delay(150, TestContext.Current.CancellationToken);

        host.Refreshes.Should().ContainSingle().Which.FileName.Should().Be("a.zip");
    }

    [Fact]
    public async Task It_asks_again_after_the_job_has_run_in_between()
    {
        var (jobs, host, _) = Start();
        var job = Paused(PauseReason.AuthRequired);
        jobs.Stored[job.Id] = job;

        jobs.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Paused, null));
        await WaitUntilAsync(() => host.Refreshes.Count == 1);
        jobs.Raise(new JobStateChanged(job.Id, JobState.Paused, JobState.Downloading, null));
        jobs.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Paused, null));

        await WaitUntilAsync(() => host.Refreshes.Count == 2);
    }

    [Theory]
    [InlineData(PauseReason.UserRequested, "https://files.example.org/page")]
    [InlineData(PauseReason.NetworkOffline, "https://files.example.org/page")]
    [InlineData(PauseReason.AuthRequired, null)]
    public async Task Other_pauses_and_jobs_without_a_page_to_go_back_to_are_left_alone(PauseReason reason, string? referrer)
    {
        var (jobs, host, _) = Start();
        var job = Paused(reason, referrer);
        jobs.Stored[job.Id] = job;

        jobs.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Paused, null));
        await Task.Delay(200, TestContext.Current.CancellationToken);

        host.Refreshes.Should().BeEmpty();
    }

    [Fact]
    public async Task The_menu_item_opens_the_dialog_for_a_chosen_paused_download()
    {
        var (jobs, host, coordinator) = Start();
        var job = Paused(PauseReason.UserRequested);
        jobs.Stored[job.Id] = job;

        coordinator.RequestRefresh(job.Id);

        await WaitUntilAsync(() => host.Refreshes.Count == 1);
    }
}
