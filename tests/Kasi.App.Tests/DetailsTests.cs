using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Core;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-UI-003")]
public class DetailsViewModelTests
{
    private static async Task<(DetailsViewModel Details, EventingJobService Service, JobRowViewModel Row, ShellSpy Shell, Job Job)> OpenAsync(
        JobState state = JobState.Downloading,
        string url = "https://files.example.org/dl/ubuntu.iso?token=SECRET-TOKEN#frag")
    {
        var job = TestJobs.New("ubuntu.iso", state, total: 6_000_000_000, done: 3_000_000_000) with { Url = url, FinalUrl = url, Folder = @"D:\Downloads\Programs" };
        var service = new EventingJobService();
        service.Stored[job.Id] = job;
        var row = new JobRowViewModel(job);
        var shell = new ShellSpy();
        var details = new DetailsViewModel(row, service, shell, action => action());
        await details.StartAsync(TestContext.Current.CancellationToken);
        return (details, service, row, shell, job);
    }

    private static SegmentsProgressed Shape(JobId id, params (long Start, long End, long Written)[] segments) =>
        new(id, [.. segments.Select((s, i) => new Segment(i, s.Start, s.End, s.Written))], [.. segments.Select((s, i) => new ConnectionView(i, i, s.Start, s.End, 1_000_000 * (i + 1)))]);

    [Fact]
    public async Task It_shows_the_file_where_it_is_saved_and_the_address_without_its_query()
    {
        var (details, _, _, _, _) = await OpenAsync();

        details.Address.Should().Be("https://files.example.org/dl/ubuntu.iso");
        details.Address.Should().NotContain("SECRET");
        details.SavingToText.Should().Be(@"Saving to D:\Downloads\Programs");
        details.ResumeSupportText.Should().Be("Supported");
        details.Row.FileName.Should().Be("ubuntu.iso");
        details.Title.Should().Be("Download details");
    }

    [Fact]
    public async Task A_server_that_cannot_resume_says_so()
    {
        var job = TestJobs.New("a.zip", JobState.Downloading) with { Resumable = false };
        var service = new EventingJobService();
        service.Stored[job.Id] = job;
        using var details = new DetailsViewModel(new JobRowViewModel(job), service, new ShellSpy(), action => action());

        await details.StartAsync(TestContext.Current.CancellationToken);

        details.ResumeSupportText.Should().Be("Not supported");
    }

    [Fact]
    [Trait("TC", "TC-UI-002")]
    public async Task Every_snapshot_the_engine_publishes_moves_the_map_at_once_with_no_coalescing()
    {
        var (details, service, row, _, _) = await OpenAsync();
        var seen = new List<long>();
        details.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DetailsViewModel.Segments))
            {
                seen.Add(details.Segments.Sum(s => s.Written));
            }
        };

        service.Raise(Shape(row.Id, (0, 1000, 100), (1000, 2000, 0)));
        service.Raise(Shape(row.Id, (0, 1000, 300), (1000, 2000, 100)));
        service.Raise(Shape(row.Id, (0, 1000, 600), (1000, 2000, 250)));

        seen.Should().Equal(100, 400, 850);
        details.MapSummaryText.Should().Be("2 connections, 42% complete");
    }

    [Fact]
    public async Task The_connection_table_has_one_line_per_live_connection()
    {
        var (details, service, row, _, _) = await OpenAsync();

        service.Raise(Shape(row.Id, (0, 1_000_000_000, 500_000_000), (1_000_000_000, 2_000_000_000, 0)));

        details.Connections.Should().HaveCount(2);
        details.Connections[0].Connection.Should().Be("1");
        details.Connections[0].Range.Should().Be("0 B - 953.7 MB");
        details.Connections[0].Speed.Should().Be("976.6 KB/s");
        details.Connections[0].State.Should().Be("Receiving");
        service.Raise(Shape(row.Id, (0, 2_000_000_000, 0)));
        details.Connections.Should().ContainSingle("the table is replaced, not appended to");
    }

    [Fact]
    public async Task Events_for_other_downloads_are_ignored()
    {
        var (details, service, _, _, _) = await OpenAsync();

        service.Raise(Shape(JobId.New(TimeProvider.System), (0, 100, 50)));
        service.Raise(new JobLogged(JobId.New(TimeProvider.System), DateTimeOffset.UtcNow, "Connections: 8"));

        details.Segments.Should().BeEmpty();
        details.Log.Should().BeEmpty();
    }

    [Fact]
    public async Task The_log_shows_the_newest_line_first_includes_state_changes_and_keeps_the_last_hundred()
    {
        var (details, service, row, _, _) = await OpenAsync();

        service.Raise(new JobLogged(row.Id, DateTimeOffset.UtcNow, "Probe: ranges supported, 6000000000 bytes"));
        service.Raise(new JobLogged(row.Id, DateTimeOffset.UtcNow, "Connections: 8"));
        service.Raise(new JobStateChanged(row.Id, JobState.Downloading, JobState.Paused, null));

        details.Log.Should().HaveCount(3);
        details.Log[0].Should().EndWith("Paused");
        details.Log[1].Should().EndWith("Connections: 8");
        details.Log[2].Should().EndWith("Probe: ranges supported, 6000000000 bytes");
        for (var i = 0; i < 150; i++)
        {
            service.Raise(new JobLogged(row.Id, DateTimeOffset.UtcNow, $"line {i}"));
        }

        details.Log.Should().HaveCount(DetailsViewModel.MaxLogLines);
        details.Log[0].Should().EndWith("line 149");
    }

    [Fact]
    public async Task The_buttons_follow_the_state_refresh_only_when_paused_and_pause_becomes_resume()
    {
        var (details, _, row, _, _) = await OpenAsync(JobState.Downloading);

        details.RefreshLinkCommand.CanExecute(null).Should().BeFalse();
        details.PauseOrResumeLabel.Should().Be("Pause");
        row.StateChanged(JobState.Paused);

        details.RefreshLinkCommand.CanExecute(null).Should().BeTrue();
        details.PauseOrResumeLabel.Should().Be("Resume");
        row.StateChanged(JobState.Completed);
        details.PauseOrResumeCommand.CanExecute(null).Should().BeFalse("a finished download has nothing to pause or resume");
    }

    [Fact]
    public async Task Pause_pauses_a_running_job_and_resume_resumes_a_paused_one()
    {
        var (details, service, row, _, _) = await OpenAsync(JobState.Downloading);

        await details.PauseOrResumeCommand.ExecuteAsync(null);
        row.StateChanged(JobState.Paused);
        await details.PauseOrResumeCommand.ExecuteAsync(null);

        service.Inner.Paused.Should().ContainSingle().Which.Should().Be(row.Id);
        service.Inner.Resumed.Should().ContainSingle().Which.Should().Be(row.Id);
    }

    [Fact]
    public async Task Open_folder_shows_the_file_and_refresh_link_hands_the_job_on()
    {
        var (details, _, row, shell, job) = await OpenAsync(JobState.Paused);
        var asked = new List<JobId>();
        details.RefreshLinkHandler = asked.Add;

        details.OpenFolderCommand.Execute(null);
        details.RefreshLinkCommand.Execute(null);

        asked.Should().Equal(row.Id);
        shell.Shown.Should().Equal(Path.Combine(job.Folder, job.FileName));
    }

    [Fact]
    public async Task Closing_the_window_stops_listening()
    {
        var (details, service, _, _, _) = await OpenAsync();

        details.Dispose();

        service.Listeners.Should().Be(0);
    }

    [Fact]
    public async Task Close_asks_the_window_to_close()
    {
        var (details, _, _, _, _) = await OpenAsync();
        var closed = 0;
        details.Closed += () => closed++;

        details.CloseCommand.Execute(null);

        closed.Should().Be(1);
    }

    [Fact]
    public void The_address_loses_its_query_and_fragment()
    {
        DetailsViewModel.WithoutQuery("https://a.example/x?y=1").Should().Be("https://a.example/x");
        DetailsViewModel.WithoutQuery("https://a.example/x#top").Should().Be("https://a.example/x");
        DetailsViewModel.WithoutQuery("https://a.example/x").Should().Be("https://a.example/x");
    }
}

[Trait("Requirement", "KASI-UI-003")]
public class DetailsWindowTests
{
    [AvaloniaFact]
    public async Task The_window_names_its_controls_and_shows_the_map_and_the_connections()
    {
        var job = TestJobs.New("ubuntu.iso", JobState.Downloading, total: 6_000_000_000, done: 3_000_000_000);
        var service = new EventingJobService();
        service.Stored[job.Id] = job;
        using var details = new DetailsViewModel(new JobRowViewModel(job), service, new ShellSpy(), action => action());
        await details.StartAsync(TestContext.Current.CancellationToken);
        var window = new DetailsWindow { DataContext = details };
        window.Show();

        service.Raise(new SegmentsProgressed(
            job.Id,
            [.. Enumerable.Range(0, 200).Select(i => new Segment(i, i * 100, (i + 1) * 100, i % 2 == 0 ? 100 : 40))],
            [new ConnectionView(0, 0, 0, 100, 5_000_000)]));

        AutomationProperties.GetName(window).Should().Be("Download details window");
        var map = window.GetVisualDescendants().OfType<SegmentMapControl>().Single();
        map.Segments.Should().HaveCount(200);
        AutomationProperties.GetName(map).Should().Contain("1 connections");
        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToList();
        buttons.Should().Contain(["Refresh link", "Open folder", "Pause", "Close"]);
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("Saving to C:\\Downloads");
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text is not null && t.Text.Contains("SECRET")).Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task The_map_draws_with_no_segments_and_with_thousands()
    {
        var job = TestJobs.New("a.bin", JobState.Downloading);
        var service = new EventingJobService();
        service.Stored[job.Id] = job;
        using var details = new DetailsViewModel(new JobRowViewModel(job), service, new ShellSpy(), action => action());
        await details.StartAsync(TestContext.Current.CancellationToken);
        var window = new DetailsWindow { DataContext = details };
        window.Show();

        var empty = () => window.UpdateLayout();
        empty.Should().NotThrow();
        service.Raise(new SegmentsProgressed(job.Id, [.. Enumerable.Range(0, 5000).Select(i => new Segment(i, i * 10, (i + 1) * 10, 5))], []));

        var again = () => window.UpdateLayout();
        again.Should().NotThrow();
    }
}
