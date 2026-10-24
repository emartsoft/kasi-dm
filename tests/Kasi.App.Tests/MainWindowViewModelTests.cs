using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Tray;
using Kasi.App.ViewModels;
using Kasi.Core;

namespace Kasi.App.Tests;

internal sealed class FakeConfirm : IConfirmHost
{
    public DeleteChoice? Answer { get; set; } = new(false, false);

    public List<ConfirmDeleteViewModel> Asked { get; } = [];

    public Task<DeleteChoice?> ConfirmDeleteAsync(ConfirmDeleteViewModel dialog)
    {
        Asked.Add(dialog);
        return Task.FromResult(Answer);
    }
}

internal sealed class MemoryUiSettings : IUiSettings
{
    public UiSettings Current { get; private set; } = new();

    /// <summary>Sets the settings without a save (no event), as if they had been on disk before the run.</summary>
    public void Set(UiSettings settings) => Current = settings;

    public event Action<UiSettings>? Changed;

    public Task SaveAsync(UiSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        Changed?.Invoke(settings);
        return Task.CompletedTask;
    }
}

internal sealed class ShellSpy : IShell
{
    public List<string> Opened { get; } = [];

    public void OpenFile(string path) => Opened.Add(path);

    public List<string> Shown { get; } = [];

    public void ShowInFolder(string path) => Shown.Add(path);
}

internal sealed class MainRig : IAsyncDisposable
{
    public required MainWindowViewModel Model { get; init; }

    public required JobListViewModel List { get; init; }

    public required SidebarViewModel Sidebar { get; init; }

    public required EventingJobService Service { get; init; }

    public required FakeConfirm Confirm { get; init; }

    public required MemoryUiSettings Ui { get; init; }

    public required ShellSpy Shell { get; init; }

    public static async Task<MainRig> CreateAsync(params Job[] jobs)
    {
        var service = new EventingJobService();
        foreach (var job in jobs)
        {
            service.Stored[job.Id] = job;
        }

        var list = new JobListViewModel(service, action => action());
        var sidebar = new SidebarViewModel(list, service);
        var confirm = new FakeConfirm();
        var ui = new MemoryUiSettings();
        var shell = new ShellSpy();
        var model = new MainWindowViewModel(list, service, sidebar, ui, confirm, shell, new SpeedLimitState(service), new ColumnLayoutViewModel(ui));
        await list.StartAsync(TestContext.Current.CancellationToken);
        await sidebar.LoadAsync(TestContext.Current.CancellationToken);
        return new MainRig { Model = model, List = list, Sidebar = sidebar, Service = service, Confirm = confirm, Ui = ui, Shell = shell };
    }

    public void Select(params string[] names) =>
        Model.SetSelection(List.All.Where(r => names.Contains(r.FileName)));

    public async ValueTask DisposeAsync()
    {
        Model.Dispose();
        await List.DisposeAsync();
    }
}

[Trait("Requirement", "KASI-UI-001")]
public class SidebarTests
{
    [Fact]
    public async Task It_lists_the_status_filters_with_counts_then_the_categories_and_the_queues()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip", JobState.Downloading), TestJobs.New("b.pdf", JobState.Completed));

        var entries = rig.Sidebar.Entries.Select(e => (e.Label, e.IsHeader)).ToList();

        entries.Take(5).Should().Equal(("STATUS", true), ("All downloads", false), ("Active", false), ("Finished", false), ("Unfinished", false));
        entries.Should().Contain(("CATEGORIES", true)).And.Contain(("Compressed", false)).And.Contain(("QUEUES", true)).And.Contain(("Main", false));
        rig.Sidebar.Entries.Where(e => e.Filter is { Status: StatusFilter.All, CategoryId: null, QueueId: null }).Select(e => e.CountText).Should().Equal("2");
        rig.Sidebar.Entries.Single(e => e.Label == "Active").CountText.Should().Be("1");
        rig.Sidebar.Entries.Single(e => e.Label == "Finished").CountText.Should().Be("1");
    }

    [Fact]
    public async Task Choosing_an_entry_filters_the_list_and_a_heading_cannot_be_chosen()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip", JobState.Downloading, category: 2), TestJobs.New("b.pdf", JobState.Completed, category: 3));

        rig.Sidebar.Selected = rig.Sidebar.Entries.Single(e => e.Label == "Compressed");
        rig.List.Visible.Select(r => r.FileName).Should().Equal("a.zip");
        rig.Sidebar.Selected = rig.Sidebar.Entries.First(e => e.IsHeader);

        rig.Sidebar.Selected!.Label.Should().Be("Compressed", "a heading is not a place to be");
        rig.List.Visible.Select(r => r.FileName).Should().Equal("a.zip");
    }

    [Fact]
    public async Task Counts_follow_the_list()
    {
        var job = TestJobs.New("a.zip", JobState.Downloading);
        await using var rig = await MainRig.CreateAsync(job);

        rig.Service.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Completed, null));

        rig.Sidebar.Entries.Single(e => e.Label == "Active").CountText.Should().Be("0");
        rig.Sidebar.Entries.Single(e => e.Label == "Finished").CountText.Should().Be("1");
    }
}

[Trait("Requirement", "KASI-UI-001")]
public class MainWindowViewModelTests
{
    [Fact]
    public async Task Resume_starts_queued_jobs_and_resumes_paused_ones_and_leaves_the_rest()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("q.zip", JobState.Queued), TestJobs.New("p.zip", JobState.Paused), TestJobs.New("d.zip", JobState.Downloading), TestJobs.New("c.zip", JobState.Completed));
        rig.Select("q.zip", "p.zip", "d.zip", "c.zip");

        await rig.Model.ResumeCommand.ExecuteAsync(null);

        rig.Service.Inner.Started.Should().ContainSingle();
        rig.Service.Inner.Resumed.Should().ContainSingle();
        rig.Service.Inner.Paused.Should().BeEmpty();
    }

    [Fact]
    public async Task Pause_and_stop_pause_what_is_running_and_nothing_else()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("d.zip", JobState.Downloading), TestJobs.New("p.zip", JobState.Paused));
        rig.Select("d.zip", "p.zip");

        await rig.Model.PauseCommand.ExecuteAsync(null);
        await rig.Model.StopCommand.ExecuteAsync(null);

        rig.Service.Inner.Paused.Should().HaveCount(2).And.OnlyContain(id => id == rig.List.All.Single(r => r.FileName == "d.zip").Id);
        rig.Service.Inner.Resumed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_buttons_are_off_when_they_have_nothing_to_act_on()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("c.zip", JobState.Completed), TestJobs.New("d.zip", JobState.Downloading));

        rig.Model.ResumeCommand.CanExecute(null).Should().BeFalse();
        rig.Model.PauseCommand.CanExecute(null).Should().BeFalse();
        rig.Model.DeleteCommand.CanExecute(null).Should().BeFalse();
        rig.Select("d.zip");
        rig.Model.PauseCommand.CanExecute(null).Should().BeTrue();
        rig.Model.ResumeCommand.CanExecute(null).Should().BeFalse();
        rig.Model.DeleteCommand.CanExecute(null).Should().BeTrue();
        rig.Select("c.zip");
        rig.Model.PauseCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task The_buttons_are_told_when_the_state_of_a_selected_row_changes()
    {
        var job = TestJobs.New("d.zip", JobState.Downloading);
        await using var rig = await MainRig.CreateAsync(job);
        rig.Select("d.zip");
        var resumeChanged = 0;
        var pauseChanged = 0;
        rig.Model.ResumeCommand.CanExecuteChanged += (_, _) => resumeChanged++;
        rig.Model.PauseCommand.CanExecuteChanged += (_, _) => pauseChanged++;

        rig.Service.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Paused, null));

        resumeChanged.Should().BePositive("a button only follows its command when the command says it changed (found by the UI smoke test: Resume stayed disabled)");
        pauseChanged.Should().BePositive();
        rig.Model.ResumeCommand.CanExecute(null).Should().BeTrue();
        rig.Model.PauseCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task The_queue_and_scheduler_buttons_are_active_and_delegate_to_service_and_dialog()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("q.zip", JobState.Queued));
        rig.Select("q.zip");

        rig.Model.StartQueueCommand.CanExecute(null).Should().BeTrue();
        rig.Model.StopQueueCommand.CanExecute(null).Should().BeTrue();
        rig.Model.SchedulerCommand.CanExecute(null).Should().BeTrue();

        bool schedulerOpened = false;
        rig.Model.SchedulerHandler = () => schedulerOpened = true;

        await rig.Model.StartQueueCommand.ExecuteAsync(null);
        rig.Service.Inner.StartedQueues.Should().Contain(1);

        await rig.Model.StopQueueCommand.ExecuteAsync(null);
        rig.Service.Inner.StoppedQueues.Should().Contain(1);

        rig.Model.SchedulerCommand.Execute(null);
        schedulerOpened.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_asks_first_and_only_a_yes_removes_the_jobs_with_or_without_their_files()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip", JobState.Paused), TestJobs.New("b.zip", JobState.Paused));
        rig.Select("a.zip");
        rig.Confirm.Answer = null;

        await rig.Model.DeleteCommand.ExecuteAsync(null);
        rig.Service.Inner.Cancelled.Should().BeEmpty("the user said no");
        rig.Confirm.Answer = new DeleteChoice(AlsoDeleteFiles: true, DontAskAgain: false);
        await rig.Model.DeleteCommand.ExecuteAsync(null);

        rig.Confirm.Asked.Should().HaveCount(2);
        rig.Confirm.Asked[0].Message.Should().Contain("a.zip");
        rig.Confirm.Asked[0].OfferFiles.Should().BeTrue();
        rig.Service.Inner.Cancelled.Should().ContainSingle().Which.DeleteFiles.Should().BeTrue();
    }

    [Fact]
    public async Task Do_not_ask_again_is_remembered_and_the_next_delete_goes_straight_through()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip", JobState.Paused), TestJobs.New("b.zip", JobState.Paused));
        rig.Confirm.Answer = new DeleteChoice(false, DontAskAgain: true);
        rig.Select("a.zip");

        await rig.Model.DeleteCommand.ExecuteAsync(null);
        rig.Select("b.zip");
        await rig.Model.DeleteCommand.ExecuteAsync(null);

        rig.Ui.Current.ConfirmDelete.Should().BeFalse();
        rig.Confirm.Asked.Should().ContainSingle("only the first delete asked");
        rig.Service.Inner.Cancelled.Should().HaveCount(2).And.OnlyContain(c => !c.DeleteFiles);
    }

    [Fact]
    public async Task Several_selected_jobs_are_asked_about_once_and_all_removed()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip", JobState.Paused), TestJobs.New("b.zip", JobState.Paused), TestJobs.New("c.zip", JobState.Paused));
        rig.Select("a.zip", "b.zip", "c.zip");

        await rig.Model.DeleteCommand.ExecuteAsync(null);

        rig.Confirm.Asked.Should().ContainSingle().Which.Message.Should().Contain("3");
        rig.Service.Inner.Cancelled.Should().HaveCount(3);
    }

    [Fact]
    public async Task Delete_completed_removes_only_completed_entries_and_never_their_files()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("c1.zip", JobState.Completed), TestJobs.New("c2.zip", JobState.Completed), TestJobs.New("d.zip", JobState.Downloading));
        rig.Confirm.Answer = new DeleteChoice(AlsoDeleteFiles: true, false);

        await rig.Model.DeleteCompletedCommand.ExecuteAsync(null);

        rig.Confirm.Asked.Should().ContainSingle().Which.OfferFiles.Should().BeFalse();
        rig.Service.Inner.Cancelled.Should().HaveCount(2).And.OnlyContain(c => !c.DeleteFiles);
    }

    [Fact]
    public async Task Delete_completed_with_nothing_completed_asks_nothing()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("d.zip", JobState.Downloading));

        await rig.Model.DeleteCompletedCommand.ExecuteAsync(null);

        rig.Confirm.Asked.Should().BeEmpty();
    }

    [Fact]
    public async Task Space_pauses_what_runs_else_resumes_what_is_paused()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("d.zip", JobState.Downloading), TestJobs.New("p.zip", JobState.Paused));

        rig.Select("d.zip");
        await rig.Model.ToggleCommand.ExecuteAsync(null);
        rig.Select("p.zip");
        await rig.Model.ToggleCommand.ExecuteAsync(null);

        rig.Service.Inner.Paused.Should().ContainSingle();
        rig.Service.Inner.Resumed.Should().ContainSingle();
    }

    [Fact]
    public async Task Opening_a_completed_job_opens_its_file_and_a_running_one_asks_for_its_details()
    {
        var done = TestJobs.New("done.pdf", JobState.Completed);
        await using var rig = await MainRig.CreateAsync(done, TestJobs.New("run.iso", JobState.Downloading));
        var details = new List<JobRowViewModel>();
        rig.Model.DetailsRequested += details.Add;

        await rig.Model.OpenRowCommand.ExecuteAsync(rig.List.Find(done.Id));
        await rig.Model.OpenRowCommand.ExecuteAsync(rig.List.All.Single(r => r.FileName == "run.iso"));

        rig.Shell.Opened.Should().Equal(@"C:\Downloads\done.pdf");
        details.Should().ContainSingle().Which.FileName.Should().Be("run.iso");
    }

    [Fact]
    public async Task The_toolbar_hands_add_url_and_settings_to_the_app()
    {
        await using var rig = await MainRig.CreateAsync();
        var adds = 0;
        var settings = 0;
        rig.Model.AddUrlHandler = () => adds++;
        rig.Model.SettingsHandler = () => settings++;

        rig.Model.AddUrlCommand.Execute(null);
        rig.Model.OpenSettingsCommand.Execute(null);

        (adds, settings).Should().Be((1, 1));
    }

    [Fact]
    public async Task The_speed_limit_is_shared_with_the_tray_and_shown_in_the_toolbar_and_the_status_bar()
    {
        await using var rig = await MainRig.CreateAsync();
        var ten = rig.Model.SpeedPresets.Single(p => p.BytesPerSecond == 10 * 1024 * 1024);

        rig.Model.SetSpeedLimitCommand.Execute(ten);

        rig.Service.SpeedLimit.Should().Be(10 * 1024 * 1024);
        rig.Model.SpeedLimitText.Should().Be("Speed limit: 10 MB/s");
        rig.Model.ToolbarSpeedLimitText.Should().Be("Speed limit: 10 MB/s");
        rig.Model.SetSpeedLimitCommand.Execute(rig.Model.SpeedPresets[0]);
        rig.Model.SpeedLimitText.Should().Be("Speed limit: Unlimited");
    }

    [Fact]
    public async Task The_status_bar_shows_counts_and_whether_a_browser_extension_is_connected()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("d.zip", JobState.Downloading), TestJobs.New("q.zip", JobState.Queued));
        var hosts = 0;
        rig.Model.ConnectedBrowsers = () => hosts;

        rig.Model.Poll();
        var none = rig.Model.ExtensionStatusText;
        hosts = 1;
        rig.Model.Poll();

        rig.Model.CountsText.Should().Be("1 active, 1 queued");
        none.Should().Be("No browser extension connected");
        rig.Model.ExtensionStatusText.Should().Be("Browser extension connected");
    }

    [Fact]
    public async Task Refresh_link_is_offered_only_for_a_paused_selection_and_hands_the_job_on()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("d.zip", JobState.Downloading), TestJobs.New("p.zip", JobState.Paused));
        var asked = new List<JobId>();
        rig.Model.RefreshLinkHandler = asked.Add;

        rig.Select("d.zip");
        rig.Model.RefreshLinkCommand.CanExecute(null).Should().BeFalse();
        rig.Select("p.zip");
        rig.Model.RefreshLinkCommand.CanExecute(null).Should().BeTrue();
        rig.Model.RefreshLinkCommand.Execute(null);

        asked.Should().Equal(rig.List.All.Single(r => r.FileName == "p.zip").Id);
    }

    [Fact]
    public async Task Ctrl_F_asks_the_window_to_focus_the_search_box()
    {
        await using var rig = await MainRig.CreateAsync();
        var focused = 0;
        rig.Model.SearchRequested += () => focused++;

        rig.Model.FocusSearchCommand.Execute(null);

        focused.Should().Be(1);
    }
}
