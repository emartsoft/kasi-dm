using Kasi.App.Controls;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.Tests;

/// <summary>UIR-1 (docs/plans/ui-redesign.md): the data behind the redesigned main window shell. KASI-UI-001.</summary>
[Trait("Requirement", "KASI-UI-001")]
public class MainShellTests
{
    [Fact]
    public void The_speed_history_keeps_the_last_sixty_samples_oldest_first_and_never_a_negative_one()
    {
        var history = new SpeedHistory();
        for (var i = 1; i <= 65; i++)
        {
            history.Add(i);
        }

        var samples = history.Snapshot();

        samples.Should().HaveCount(60);
        samples[0].Should().Be(6);
        samples[^1].Should().Be(65);
        history.Add(-5);
        history.Snapshot()[^1].Should().Be(0);
        samples[^1].Should().Be(65, "a snapshot is a copy: the chart never sees the list change under it");
    }

    [Fact]
    public async Task Each_poll_takes_a_sample_of_the_total_speed_and_shows_it_as_the_big_number()
    {
        var job = TestJobs.New("a.iso", JobState.Downloading, total: 10_000_000);
        await using var rig = await MainRig.CreateAsync(job);
        rig.Model.Poll();
        rig.Model.Jobs.SpeedSamples.Should().Equal([0.0]);
        rig.Model.Jobs.SpeedNumberText.Should().Be("0 B/s");

        rig.Service.Raise(new JobProgressed(job.Id, 1_000_000, 10_000_000, 2_097_152));
        await Eventually(() => rig.Model.Jobs.TotalBytesPerSecond > 0);
        rig.Model.Poll();

        rig.Model.Jobs.SpeedSamples.Should().Equal([0.0, 2_097_152.0]);
        rig.Model.Jobs.SpeedNumberText.Should().Be("2 MB/s");
    }

    [Fact]
    public async Task The_status_bar_says_how_much_room_the_download_drive_has_and_asks_only_now_and_then()
    {
        await using var rig = await MainRig.CreateAsync();
        var asked = 0;
        rig.Model.DiskFree = () =>
        {
            asked++;
            return (@"C:", 412L * 1024 * 1024 * 1024);
        };

        rig.Model.Poll();
        for (var i = 0; i < 4; i++)
        {
            rig.Model.Poll();
        }

        rig.Model.DiskFreeText.Should().Be("Disk (C:): 412 GB free");
        asked.Should().Be(1, "the disk is read every fifth second, not every second");
        rig.Model.Poll();
        asked.Should().Be(2);
        rig.Model.DiskFree = () => null;
        for (var i = 0; i < 5; i++)
        {
            rig.Model.Poll();
        }

        rig.Model.DiskFreeText.Should().BeEmpty("a drive that cannot be read says nothing");
    }

    [Fact]
    public async Task The_extension_card_follows_the_connected_browsers()
    {
        await using var rig = await MainRig.CreateAsync();
        var hosts = 0;
        rig.Model.ConnectedBrowsers = () => hosts;

        rig.Model.Poll();
        rig.Model.ExtensionConnected.Should().BeFalse();
        rig.Model.ExtensionCardText.Should().Be("Not connected");
        hosts = 2;
        rig.Model.Poll();

        rig.Model.ExtensionConnected.Should().BeTrue();
        rig.Model.ExtensionCardText.Should().Be("Connected");
    }

    [Fact]
    public async Task The_status_dot_is_lit_while_something_runs()
    {
        var job = TestJobs.New("a.iso", JobState.Downloading);
        await using var rig = await MainRig.CreateAsync(job);
        rig.Model.Jobs.HasActive.Should().BeTrue();

        rig.Service.Raise(new JobStateChanged(job.Id, JobState.Downloading, JobState.Completed, null));

        rig.Model.Jobs.HasActive.Should().BeFalse();
    }

    [Fact]
    public async Task The_sidebar_gives_each_entry_its_icon_and_colour_and_each_queue_its_schedule_window()
    {
        await using var rig = await MainRig.CreateAsync();
        rig.Service.Inner.CategoryList = [new Category { Id = 1, Name = "Video", Folder = "Video" }, new Category { Id = 2, Name = "Documents", Folder = "Documents" }];
        rig.Service.Inner.Schedules[1] = new QueueSchedule { QueueId = 1, IsEnabled = true, StartTime = new TimeOnly(2, 0), StopTime = new TimeOnly(7, 0) };
        await rig.Sidebar.LoadAsync(TestContext.Current.CancellationToken);

        var byLabel = rig.Sidebar.Entries.Where(e => !e.IsHeader).GroupBy(e => e.Label).ToDictionary(g => g.Key, g => g.Last());

        byLabel["All downloads"].Glyph.Should().Be(Icons.List);
        byLabel["Active"].GlyphKind.Should().Be(ChipKind.Success);
        byLabel["Finished"].GlyphKind.Should().Be(ChipKind.Accent);
        byLabel["Unfinished"].GlyphKind.Should().Be(ChipKind.Warning);
        byLabel["Video"].Glyph.Should().Be(Icons.Video);
        byLabel["Documents"].Glyph.Should().Be(Icons.Document);
        byLabel["Main"].Detail.Should().Be("02:00 - 07:00");
        byLabel["Main"].HasDetail.Should().BeTrue();
        byLabel["Video"].HasDetail.Should().BeFalse();
        rig.Sidebar.Entries.Where(e => e.IsHeader).Should().OnlyContain(e => e.Glyph == null && !e.HasCount);
    }

    [Theory]
    [InlineData("Compressed", Icons.Package)]
    [InlineData("Music", Icons.Music)]
    [InlineData("Programs", Icons.Apps)]
    [InlineData("Images", Icons.Picture)]
    [InlineData("Torrents", Icons.Download)]
    [InlineData("My own category", Icons.Folder)]
    public void A_category_has_an_icon_by_name_and_a_plain_folder_when_it_is_the_persons_own(string name, string glyph)
    {
        SidebarViewModel.GlyphForCategory(name).Should().Be(glyph);
    }

    [Fact]
    public void A_schedule_reads_as_a_window_or_a_start_time_and_a_switched_off_one_says_nothing()
    {
        var window = new QueueSchedule { QueueId = 1, IsEnabled = true, StartTime = new TimeOnly(23, 0), StopTime = new TimeOnly(6, 30) };

        SidebarViewModel.ScheduleText(window).Should().Be("23:00 - 06:30");
        SidebarViewModel.ScheduleText(window with { StopTime = null }).Should().Be("from 23:00");
        SidebarViewModel.ScheduleText(window with { IsEnabled = false }).Should().BeNull();
        SidebarViewModel.ScheduleText(null).Should().BeNull();
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The condition did not become true.");
    }
}
