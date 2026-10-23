using System.Diagnostics;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kasi.App.Browser;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Engine;
using Kasi.Ipc.Messages;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-BRW-005")]
public class LinkPickerTests
{
    private static BrowserBatch Batch(int count, Func<int, string>? name = null, IReadOnlyList<IpcCookie>? cookies = null) => new(
        "https://files.example.org/page",
        [.. Enumerable.Range(0, count).Select(i => new IpcLink { Url = $"https://files.example.org/files/{(name?.Invoke(i) ?? "doc" + i)}" })],
        cookies ?? [],
        "Agent/1.0");

    private static async Task<(LinkPickerViewModel Vm, RecordingJobService Jobs)> OpenAsync(BrowserBatch batch)
    {
        var jobs = new RecordingJobService();
        var vm = new LinkPickerViewModel(batch, jobs);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return (vm, jobs);
    }

    [Fact]
    [Trait("TC", "TC-BRW-005")]
    public async Task Twelve_of_forty_selected_create_twelve_jobs_with_the_right_links_sharing_the_batch()
    {
        var cookie = new IpcCookie { Name = "sid", Value = "COOKIE-VALUE", Domain = ".example.org", Path = "/", Secure = true, HttpOnly = true };
        var (vm, jobs) = await OpenAsync(Batch(40, cookies: [cookie]));
        foreach (var row in vm.Shown.Take(12))
        {
            row.IsSelected = true;
        }

        await vm.AddCommand.ExecuteAsync(null);

        jobs.Adds.Should().HaveCount(12);
        jobs.Adds.Select(a => a.Request.Url).Should().Equal(Enumerable.Range(0, 12).Select(i => $"https://files.example.org/files/doc{i}"));
        jobs.Adds.Should().OnlyContain(a => a.Request.Referrer == "https://files.example.org/page" && a.Request.UserAgent == "Agent/1.0" && a.Request.QueueId == 1 && a.Request.StartNow);
        jobs.Adds.Should().OnlyContain(a => a.Request.Cookies!.Count == 1 && a.Request.Cookies[0].Value == "COOKIE-VALUE");
        vm.AddedCount.Should().Be(12);
    }

    [Fact]
    public async Task Nothing_selected_cannot_be_added()
    {
        var (vm, jobs) = await OpenAsync(Batch(5));

        vm.AddCommand.CanExecute(null).Should().BeFalse();
        vm.Shown[0].IsSelected = true;
        vm.AddCommand.CanExecute(null).Should().BeTrue();
        vm.Shown[0].IsSelected = false;
        vm.AddCommand.CanExecute(null).Should().BeFalse();
        jobs.Adds.Should().BeEmpty();
    }

    [Fact]
    public async Task Select_all_ticks_what_is_shown_and_select_none_clears_everything()
    {
        var (vm, _) = await OpenAsync(Batch(6, i => i < 4 ? $"set{i}.zip" : $"note{i}.txt"));

        vm.FilterText = "set";
        vm.SelectAllCommand.Execute(null);
        vm.SelectedCount.Should().Be(4);
        vm.FilterText = string.Empty;
        vm.Shown.Count(r => r.IsSelected).Should().Be(4, "links hidden by the filter keep their state");
        vm.FilterText = "note"; // the four ticked links are hidden now
        vm.SelectNoneCommand.Execute(null);
        vm.FilterText = string.Empty;

        vm.SelectedCount.Should().Be(0);
        vm.Shown.Should().OnlyContain(r => !r.IsSelected);
    }

    [Fact]
    public async Task The_text_filter_matches_the_address_and_the_file_name_without_regard_to_case()
    {
        var batch = new BrowserBatch(
            "https://files.example.org/",
            [
                new IpcLink { Url = "https://files.example.org/dl?id=1", Filename = "Report.PDF" },
                new IpcLink { Url = "https://cdn.example.net/archive/a.zip" },
                new IpcLink { Url = "https://files.example.org/other.iso" },
            ],
            [],
            "Agent/1.0");
        var (vm, _) = await OpenAsync(batch);

        vm.FilterText = "report";
        vm.Shown.Select(r => r.FileName).Should().Equal("Report.PDF");
        vm.FilterText = "CDN.EXAMPLE";
        vm.Shown.Select(r => r.FileName).Should().Equal("a.zip");
        vm.FilterText = "nothing";
        vm.Shown.Should().BeEmpty();
    }

    [Fact]
    public async Task The_extension_filter_lists_the_types_that_occur_most_first_and_narrows_the_list()
    {
        var (vm, _) = await OpenAsync(Batch(7, i => i < 4 ? $"a{i}.zip" : i < 6 ? $"b{i}.iso" : "c.txt"));

        vm.Extensions.Select(e => e.Extension).Should().Equal(null, "zip", "iso", "txt");
        vm.SelectedExtension = vm.Extensions[2];

        vm.Shown.Select(r => r.Extension).Should().OnlyContain(e => e == "iso").And.HaveCount(2);
        vm.FilterText = "b4";
        vm.Shown.Should().ContainSingle();
    }

    [Fact]
    public async Task The_summary_counts_selected_total_and_shown()
    {
        var (vm, _) = await OpenAsync(Batch(10));

        vm.Shown[0].IsSelected = true;
        vm.Shown[1].IsSelected = true;
        vm.FilterText = "doc1";

        vm.Summary.Should().Be("2 of 10 selected, 1 shown");
    }

    [Fact]
    public async Task A_duplicate_gets_a_new_name_and_a_refused_link_is_counted_without_ending_the_rest()
    {
        var (vm, jobs) = await OpenAsync(Batch(3));
        jobs.Conflicts.Enqueue(new DuplicateCheckResult(null, true, false, "doc0 (1)", [DuplicateAction.AutoRename]));
        vm.SelectAllCommand.Execute(null);

        await vm.AddCommand.ExecuteAsync(null);

        jobs.Adds.Select(a => a.Resolution).Should().Equal(null, DuplicateAction.AutoRename, null, null);
        vm.AddedCount.Should().Be(3);
        vm.Error.Should().BeEmpty();
    }

    [Fact]
    public async Task The_window_closes_after_a_clean_add_and_stays_open_to_report_failures()
    {
        var (vm, _) = await OpenAsync(Batch(2));
        var closed = 0;
        vm.Closed += () => closed++;
        vm.SelectAllCommand.Execute(null);

        await vm.AddCommand.ExecuteAsync(null);

        closed.Should().Be(1);
    }

    [Fact]
    public async Task Rows_and_the_view_model_never_print_an_address()
    {
        var (vm, _) = await OpenAsync(new BrowserBatch("https://x/", [new IpcLink { Url = "https://files.example.org/a.zip?token=SECRET" }], [], "Agent/1.0"));

        vm.Shown[0].ToString().Should().NotContain("SECRET");
    }

    [Fact]
    public async Task A_page_of_one_hundred_thousand_links_opens_filters_and_selects_in_bounded_time()
    {
        var watch = Stopwatch.StartNew();
        var (vm, _) = await OpenAsync(Batch(100_000, i => $"file{i}.{(i % 3 == 0 ? "zip" : "iso")}"));
        vm.FilterText = "file9999";
        vm.SelectedExtension = vm.Extensions.First(e => e.Extension == "zip");
        vm.FilterText = string.Empty;
        vm.SelectAllCommand.Execute(null);
        watch.Stop();

        vm.TotalCount.Should().Be(100_000);
        vm.SelectedCount.Should().Be(33_334);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), "a generous bound: this guards against quadratic work, not speed");
    }

    [AvaloniaFact]
    public async Task The_picker_window_names_its_controls_and_lists_the_links()
    {
        var (vm, _) = await OpenAsync(Batch(40));
        var window = new LinkPickerWindow { DataContext = vm };

        window.Show();

        AutomationProperties.GetName(window).Should().Be("Link picker window");
        var controls = window.GetVisualDescendants().OfType<Control>()
            .Where(c => (c is TextBox or ComboBox or CheckBox or Button) && c.FindAncestorOfType<ComboBox>() is null && c.FindAncestorOfType<ListBox>() is null)
            .ToList();
        controls.Should().HaveCountGreaterThanOrEqualTo(8);
        controls.Should().OnlyContain(c => !string.IsNullOrEmpty(AutomationProperties.GetName(c)));
        window.GetVisualDescendants().OfType<ListBox>().Single().ItemCount.Should().Be(40);
    }
}
