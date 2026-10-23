using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Engine;
using Kasi.Ipc.Messages;

namespace Kasi.App.Tests;

/// <summary>A service that records what it is asked and answers from a script.</summary>
internal sealed class RecordingJobService : IJobService
{
    public List<(NewDownload Request, DuplicateAction? Resolution)> Adds { get; } = [];

    /// <summary>Conflicts to report, in order; once empty every add succeeds.</summary>
    public Queue<DuplicateCheckResult> Conflicts { get; } = new();

    public IReadOnlyList<Category> CategoryList { get; set; } =
    [
        new Category { Id = 1, Name = "General", Folder = "", IsBuiltin = true },
        new Category { Id = 2, Name = "Compressed", Folder = "Compressed", IsBuiltin = true },
    ];

    public IReadOnlyList<Core.Queue> QueueList { get; set; } = [new Core.Queue { Id = 1, Name = "Main", SortOrder = 0 }];

#pragma warning disable CS0067 // never raised by this fake
    public event Action<EngineEvent>? EventRaised;
#pragma warning restore CS0067

    public Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Job>>([]);

    public Task<IReadOnlyList<Category>> CategoriesAsync(CancellationToken cancellationToken = default) => Task.FromResult(CategoryList);

    public Task<IReadOnlyList<Core.Queue>> QueuesAsync(CancellationToken cancellationToken = default) => Task.FromResult(QueueList);

    public Task<AddResult> AddAsync(NewDownload request, DuplicateAction? resolution = null, CancellationToken cancellationToken = default)
    {
        lock (Adds)
        {
            Adds.Add((request, resolution));
        }

        if (resolution is null && Conflicts.TryDequeue(out var conflict))
        {
            return Task.FromResult(new AddResult(conflict, null));
        }

        var job = new Job
        {
            Id = JobId.New(TimeProvider.System),
            Kind = JobKind.Http,
            Url = request.Url,
            FileName = request.FileName,
            Folder = request.Folder ?? "C:\\Downloads",
            QueueId = request.QueueId,
            QueuePos = 1,
            CreatedUtc = DateTimeOffset.UnixEpoch,
        };
        return Task.FromResult(new AddResult(new DuplicateCheckResult(null, false, false, request.FileName, []), job));
    }

    public Task PauseAsync(JobId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ResumeAsync(JobId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CancelAsync(JobId id, bool deleteFiles = false, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class MemorySites : ISiteDefaults
{
    public Dictionary<string, SiteChoice> ByHost { get; } = [];

    public SiteChoice? Get(string host) => ByHost.GetValueOrDefault(host.ToLowerInvariant());

    public Task RememberAsync(string host, SiteChoice choice, CancellationToken cancellationToken = default)
    {
        ByHost[host.ToLowerInvariant()] = choice;
        return Task.CompletedTask;
    }
}

[Trait("Requirement", "KASI-UI-002")]
public class AddDialogTests
{
    private static readonly DuplicateCheckResult _conflict = new(null, true, false, "pack (1).zip", [DuplicateAction.Overwrite, DuplicateAction.AutoRename]);

    private static async Task<(AddDownloadViewModel Vm, RecordingJobService Jobs, MemorySites Sites)> OpenAsync(
        AddDownloadDraft? draft = null,
        Func<DuplicateCheckResult, Task<DuplicateAction?>>? ask = null)
    {
        var jobs = new RecordingJobService();
        var sites = new MemorySites();
        var vm = new AddDownloadViewModel(draft ?? new AddDownloadDraft("https://files.example.org/dir/pack.zip"), jobs, sites, ask ?? (_ => Task.FromResult<DuplicateAction?>(null)));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return (vm, jobs, sites);
    }

    [Fact]
    [Trait("TC", "TC-UI-001")]
    public async Task The_dialog_opens_with_the_link_a_file_name_from_it_and_the_first_category_and_queue()
    {
        var (vm, _, _) = await OpenAsync();

        vm.Url.Should().Be("https://files.example.org/dir/pack.zip");
        vm.FileName.Should().Be("pack.zip");
        vm.SelectedCategory!.Name.Should().Be("General");
        vm.SelectedQueue!.Name.Should().Be("Main");
        vm.StartNow.Should().BeTrue();
    }

    [Fact]
    [Trait("TC", "TC-UI-001")]
    public async Task Every_field_reaches_the_request()
    {
        var hex = new string('a', 64);
        var cookie = new JobCookie { Name = "sid", Value = "COOKIE-VALUE", Domain = ".example.org" };
        var (vm, _, _) = await OpenAsync(new AddDownloadDraft("https://files.example.org/x", "x.bin", "https://files.example.org/page", [cookie], "Agent/1.0"));

        vm.Url = " https://files.example.org/other.zip ";
        vm.FileName = " renamed.zip ";
        vm.Folder = @" D:\Saved ";
        vm.SelectedCategory = vm.Categories[1];
        vm.Connections = 6;
        vm.StartNow = false;
        vm.SelectedChecksum = vm.ChecksumChoices[3];
        vm.ChecksumHex = hex;
        var request = vm.BuildRequest();

        request.Should().NotBeNull();
        request!.Url.Should().Be("https://files.example.org/other.zip");
        request.FileName.Should().Be("renamed.zip");
        request.Folder.Should().Be(@"D:\Saved");
        request.CategoryId.Should().Be(2);
        request.QueueId.Should().Be(1);
        request.Connections.Should().Be(6);
        request.StartNow.Should().BeFalse();
        request.ChecksumAlgorithm.Should().Be("sha256");
        request.Checksum.Should().Be(hex);
        request.Referrer.Should().Be("https://files.example.org/page");
        request.Cookies.Should().ContainSingle().Which.Should().BeSameAs(cookie);
        request.UserAgent.Should().Be("Agent/1.0");
    }

    [Fact]
    public async Task Empty_folder_connections_and_checksum_mean_the_defaults()
    {
        var (vm, _, _) = await OpenAsync();

        var request = vm.BuildRequest();

        request!.Folder.Should().BeNull();
        request.Connections.Should().BeNull();
        request.ChecksumAlgorithm.Should().BeNull();
        request.Checksum.Should().BeNull();
        request.Cookies.Should().BeNull();
    }

    [Theory]
    [InlineData("url", "not a link")]
    [InlineData("url", "file:///c:/x")]
    [InlineData("name", "")]
    [InlineData("name", "a/b.zip")]
    [InlineData("name", "con?.zip")]
    [InlineData("connections", "0")]
    [InlineData("connections", "33")]
    [InlineData("connections", "2.5")]
    [InlineData("hex", "abc")]
    public async Task A_field_that_is_not_valid_gives_no_request_and_an_error(string field, string value)
    {
        var (vm, jobs, _) = await OpenAsync();
        vm.SelectedChecksum = vm.ChecksumChoices[3];
        vm.ChecksumHex = new string('a', 64);
        switch (field)
        {
            case "url": vm.Url = value; break;
            case "name": vm.FileName = value; break;
            case "connections": vm.Connections = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            case "hex": vm.ChecksumHex = value; break;
        }

        vm.AddCommand.Execute(null);

        vm.BuildRequest().Should().BeNull();
        vm.Error.Should().NotBeEmpty();
        jobs.Adds.Should().BeEmpty();
    }

    [Fact]
    public async Task A_checksum_value_without_an_algorithm_is_refused()
    {
        var (vm, _, _) = await OpenAsync();

        vm.ChecksumHex = new string('a', 64);

        vm.BuildRequest().Should().BeNull();
    }

    [Fact]
    public async Task Adding_creates_the_job_and_closes_the_dialog()
    {
        var (vm, jobs, _) = await OpenAsync();
        var closed = 0;
        vm.Closed += () => closed++;

        await vm.AddCommand.ExecuteAsync(null);

        jobs.Adds.Should().ContainSingle();
        vm.Result.Should().NotBeNull();
        closed.Should().Be(1);
    }

    [Fact]
    [Trait("TC", "TC-ENG-021")]
    public async Task A_duplicate_asks_and_the_answer_is_sent_with_the_request()
    {
        DuplicateCheckResult? asked = null;
        var (vm, jobs, _) = await OpenAsync(ask: check =>
        {
            asked = check;
            return Task.FromResult<DuplicateAction?>(DuplicateAction.AutoRename);
        });
        jobs.Conflicts.Enqueue(_conflict);

        await vm.AddCommand.ExecuteAsync(null);

        asked.Should().BeSameAs(_conflict);
        jobs.Adds.Select(a => a.Resolution).Should().Equal(null, DuplicateAction.AutoRename);
        vm.Result.Should().NotBeNull();
    }

    [Fact]
    [Trait("TC", "TC-ENG-021")]
    public async Task Backing_out_of_the_duplicate_prompt_adds_nothing_and_keeps_the_dialog_open()
    {
        var (vm, jobs, _) = await OpenAsync();
        jobs.Conflicts.Enqueue(_conflict);
        var closed = 0;
        vm.Closed += () => closed++;

        await vm.AddCommand.ExecuteAsync(null);

        jobs.Adds.Should().ContainSingle();
        vm.Result.Should().BeNull();
        closed.Should().Be(0);
    }

    [Fact]
    public async Task Remember_for_this_site_keeps_the_category_and_folder_and_the_next_dialog_uses_them()
    {
        var (vm, jobs, sites) = await OpenAsync();
        vm.SelectedCategory = vm.Categories[1];
        vm.Folder = @"D:\Saved";
        vm.RememberForSite = true;

        await vm.AddCommand.ExecuteAsync(null);

        sites.ByHost["files.example.org"].Should().Be(new SiteChoice(2, @"D:\Saved"));
        var next = new AddDownloadViewModel(new AddDownloadDraft("https://files.example.org/another.zip"), jobs, sites, _ => Task.FromResult<DuplicateAction?>(null));
        await next.LoadAsync(TestContext.Current.CancellationToken);
        next.SelectedCategory!.Id.Should().Be(2);
        next.Folder.Should().Be(@"D:\Saved");
    }

    [Fact]
    public async Task Without_the_box_ticked_nothing_is_remembered()
    {
        var (vm, _, sites) = await OpenAsync();

        await vm.AddCommand.ExecuteAsync(null);

        sites.ByHost.Should().BeEmpty();
    }

    [Fact]
    public async Task The_draft_prints_only_its_type()
    {
        var cookie = new JobCookie { Name = "sid", Value = "COOKIE-VALUE", Domain = ".example.org" };
        var draft = new AddDownloadDraft("https://files.example.org/x?token=SECRET", cookies: [cookie]);

        draft.ToString().Should().Be("AddDownloadDraft");
        (await OpenAsync(draft)).Vm.BuildRequest()!.ToString().Should().Be("NewDownload");
    }

    [Fact]
    [Trait("TC", "TC-ENG-021")]
    public void The_duplicate_prompt_offers_exactly_what_the_engine_offered_and_reports_the_pick()
    {
        var prompt = new DuplicatePromptViewModel(_conflict);
        var closed = 0;
        prompt.Closed += () => closed++;

        prompt.Choices.Select(c => c.Action).Should().Equal(DuplicateAction.Overwrite, DuplicateAction.AutoRename);
        prompt.Choices[1].Label.Should().Contain("pack (1).zip");
        prompt.Choose(DuplicateAction.Overwrite);

        prompt.Chosen.Should().Be(DuplicateAction.Overwrite);
        closed.Should().Be(1);
    }

    [Fact]
    public void Cancelling_the_duplicate_prompt_picks_nothing()
    {
        var prompt = new DuplicatePromptViewModel(_conflict);

        prompt.Cancel();

        prompt.Chosen.Should().BeNull();
    }
}

public class AddCoordinatorTests
{
    private sealed class FakeHost : IAddDialogHost
    {
        public List<AddDownloadViewModel> Shown { get; } = [];

        public TaskCompletionSource Release { get; set; } = new();

        public Task ShowAddAsync(AddDownloadViewModel dialog)
        {
            lock (Shown)
            {
                Shown.Add(dialog);
            }

#pragma warning disable VSTHRD003 // the test releases it
            return Release.Task;
#pragma warning restore VSTHRD003
        }

        public Task<DuplicateAction?> AskDuplicateAsync(DuplicateCheckResult check) => Task.FromResult<DuplicateAction?>(null);
    }

    private sealed class PickerSink : IBrowserRequestSink
    {
        public List<BrowserBatch> Batches { get; } = [];

        public void ShowAdd(BrowserDownload download) => throw new NotSupportedException();

        public void ShowPicker(BrowserBatch batch) => Batches.Add(batch);
    }

    private sealed class FixedSettings(BrowserSettings current) : IBrowserSettings
    {
        public BrowserSettings Current { get; } = current;

#pragma warning disable CS0067
        public event Action<BrowserSettings>? Changed;
#pragma warning restore CS0067

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static BrowserDownload Download(string url = "https://files.example.org/pack.zip") => new(
        url,
        url,
        "https://files.example.org/",
        "pack.zip",
        "application/zip",
        "Agent/1.0",
        [new IpcCookie { Name = "sid", Value = "COOKIE-VALUE", Domain = ".example.org", Path = "/", Secure = true, HttpOnly = true }]);

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
    public async Task A_browser_download_opens_the_dialog_with_its_name_referrer_cookies_and_agent()
    {
        var jobs = new RecordingJobService();
        var host = new FakeHost();
        var coordinator = new AddCoordinator(jobs, new MemorySites(), new FixedSettings(new BrowserSettings()), host, new PickerSink());

        coordinator.ShowAdd(Download());
        await WaitUntilAsync(() => host.Shown.Count == 1);

        var request = host.Shown[0].BuildRequest()!;
        request.FileName.Should().Be("pack.zip");
        request.Referrer.Should().Be("https://files.example.org/");
        request.UserAgent.Should().Be("Agent/1.0");
        request.Cookies.Should().ContainSingle(c => c.Name == "sid");
        host.Release.SetResult();
    }

    [Fact]
    public async Task Dialogs_open_one_at_a_time_in_the_order_asked()
    {
        var host = new FakeHost();
        var coordinator = new AddCoordinator(new RecordingJobService(), new MemorySites(), new FixedSettings(new BrowserSettings()), host, new PickerSink());

        coordinator.RequestAdd(["https://files.example.org/one.zip", "https://files.example.org/two.zip"]);
        await WaitUntilAsync(() => host.Shown.Count == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        host.Shown.Should().ContainSingle("the second waits for the first to close");
        host.Release.SetResult();
        await WaitUntilAsync(() => host.Shown.Count == 2);
        host.Shown.Select(d => d.FileName).Should().Equal("one.zip", "two.zip");
    }

    [Fact]
    public async Task With_the_dialog_switched_off_a_browser_download_is_added_at_once_with_the_defaults()
    {
        var jobs = new RecordingJobService();
        var host = new FakeHost();
        var coordinator = new AddCoordinator(jobs, new MemorySites(), new FixedSettings(new BrowserSettings { ShowAddDialog = false }), host, new PickerSink());

        coordinator.ShowAdd(Download());
        await WaitUntilAsync(() => jobs.Adds.Count == 1);

        host.Shown.Should().BeEmpty();
        var (request, resolution) = jobs.Adds[0];
        request.FileName.Should().Be("pack.zip");
        request.CategoryId.Should().Be(1);
        request.StartNow.Should().BeTrue();
        request.Cookies.Should().ContainSingle();
        resolution.Should().BeNull();
    }

    [Fact]
    public async Task With_the_dialog_switched_off_a_duplicate_gets_a_new_name()
    {
        var jobs = new RecordingJobService();
        jobs.Conflicts.Enqueue(new DuplicateCheckResult(null, true, false, "pack (1).zip", [DuplicateAction.AutoRename]));
        var coordinator = new AddCoordinator(jobs, new MemorySites(), new FixedSettings(new BrowserSettings { ShowAddDialog = false }), new FakeHost(), new PickerSink());

        coordinator.ShowAdd(Download());
        await WaitUntilAsync(() => jobs.Adds.Count == 2);

        jobs.Adds.Select(a => a.Resolution).Should().Equal(null, DuplicateAction.AutoRename);
    }

    [Fact]
    public async Task A_failure_is_reported_and_the_next_request_still_runs()
    {
        var host = new FakeHost();
        var faults = new List<Exception>();
        var jobs = new RecordingJobService { QueueList = [] };
        var coordinator = new AddCoordinator(jobs, new MemorySites(), new FixedSettings(new BrowserSettings { ShowAddDialog = false }), host, new PickerSink(), faults.Add);

        coordinator.ShowAdd(Download());
        await WaitUntilAsync(() => faults.Count == 1);
        jobs.QueueList = [new Core.Queue { Id = 1, Name = "Main", SortOrder = 0 }];
        coordinator.ShowAdd(Download("https://files.example.org/next.zip"));
        await WaitUntilAsync(() => jobs.Adds.Count == 1);

        faults.Should().ContainSingle();
    }

    [Fact]
    public void A_batch_goes_to_the_picker()
    {
        var picker = new PickerSink();
        var coordinator = new AddCoordinator(new RecordingJobService(), new MemorySites(), new FixedSettings(new BrowserSettings()), new FakeHost(), picker);
        var batch = new BrowserBatch("https://files.example.org/", [], [], "Agent/1.0");

        coordinator.ShowPicker(batch);

        picker.Batches.Should().ContainSingle().Which.Should().BeSameAs(batch);
    }
}

public class SiteDefaultsStoreTests
{
    [Fact]
    public async Task A_remembered_site_survives_a_restart_and_matching_ignores_case()
    {
        var path = Path.Combine(Path.GetTempPath(), "kasi-sites-" + Guid.NewGuid().ToString("N"), "site-defaults.json");
        await new SiteDefaultsStore(path).RememberAsync("Files.Example.org", new SiteChoice(3, @"D:\Docs"), TestContext.Current.CancellationToken);

        new SiteDefaultsStore(path).Get("files.example.ORG").Should().Be(new SiteChoice(3, @"D:\Docs"));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void A_missing_or_broken_file_is_an_empty_memory()
    {
        var path = Path.Combine(Path.GetTempPath(), "kasi-sites-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{ not json");

        new SiteDefaultsStore(path).Get("files.example.org").Should().BeNull();
        new SiteDefaultsStore(path + ".missing").Get("files.example.org").Should().BeNull();
        File.Delete(path);
    }
}
