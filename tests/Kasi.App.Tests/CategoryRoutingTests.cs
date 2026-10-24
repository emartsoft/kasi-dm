using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Engine;
using Kasi.Ipc.Messages;

namespace Kasi.App.Tests;

/// <summary>M2.11b: the category rules are applied where a download is created (KASI-CAT-001, 002).</summary>
[Trait("Requirement", "KASI-CAT-001")]
public class CategoryRoutingTests
{
    private static readonly IReadOnlyList<Category> _categories =
    [
        new() { Id = 1, Name = "General", Folder = string.Empty, IsBuiltin = true },
        new() { Id = 2, Name = "Compressed", Folder = "Compressed", IsBuiltin = true },
        new() { Id = 5, Name = "Video", Folder = "Video", IsBuiltin = true },
        new() { Id = 9, Name = "Code", Folder = "Code" },
    ];

    private static readonly IReadOnlyList<CategoryRule> _rules =
    [
        new() { Id = 1, CategoryId = 2, Kind = CategoryRuleKind.Extension, Pattern = "zip", Position = 1, IsBuiltin = true },
        new() { Id = 2, CategoryId = 5, Kind = CategoryRuleKind.Extension, Pattern = "mp4", Position = 2, IsBuiltin = true },
        new() { Id = 3, CategoryId = 9, Kind = CategoryRuleKind.Domain, Pattern = "code.example.org", Position = 3 },
    ];

    private static RecordingJobService Jobs() => new() { CategoryList = _categories, Rules = _rules };

    private static async Task<AddDownloadViewModel> OpenAsync(RecordingJobService jobs, AddDownloadDraft draft, MemorySites? sites = null)
    {
        var vm = new AddDownloadViewModel(draft, jobs, sites ?? new MemorySites(), _ => Task.FromResult<DuplicateAction?>(null));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task The_dialog_opens_on_the_category_the_rules_give_the_link()
    {
        var vm = await OpenAsync(Jobs(), new AddDownloadDraft("https://files.example.org/film.mp4"));

        vm.SelectedCategory!.Name.Should().Be("Video");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_domain_rule_decides_for_the_dialog_too()
    {
        var vm = await OpenAsync(Jobs(), new AddDownloadDraft("https://code.example.org/film.mp4"));

        vm.SelectedCategory!.Name.Should().Be("Code");
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task The_MIME_type_the_browser_reported_decides_when_the_name_has_no_extension()
    {
        var rules = new List<CategoryRule>(_rules);
        var jobs = Jobs();
        jobs.Rules = rules;

        var vm = await OpenAsync(jobs, new AddDownloadDraft("https://files.example.org/get", fileName: "get", mime: "video/mp4"));

        vm.SelectedCategory!.Name.Should().Be("Video");
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task Typing_another_file_name_or_link_changes_the_category_until_the_person_chooses_one()
    {
        var vm = await OpenAsync(Jobs(), new AddDownloadDraft("https://files.example.org/pack.zip"));
        vm.SelectedCategory!.Name.Should().Be("Compressed");

        vm.FileName = "film.mp4";
        vm.SelectedCategory!.Name.Should().Be("Video");
        vm.Url = "https://code.example.org/film.mp4";
        vm.SelectedCategory!.Name.Should().Be("Code");

        vm.SelectedCategory = vm.Categories.Single(c => c.Name == "General"); // the person decides
        vm.FileName = "other.zip";
        vm.Url = "https://files.example.org/other.zip";

        vm.SelectedCategory!.Name.Should().Be("General", "a choice made by hand is not undone by typing");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_remembered_site_wins_over_the_rules()
    {
        var sites = new MemorySites();
        sites.ByHost["files.example.org"] = new SiteChoice(1, null);

        var vm = await OpenAsync(Jobs(), new AddDownloadDraft("https://files.example.org/film.mp4"), sites);
        vm.FileName = "other.zip";

        vm.SelectedCategory!.Name.Should().Be("General");
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task The_dialog_hands_its_category_and_the_MIME_type_on()
    {
        var jobs = Jobs();
        var vm = await OpenAsync(jobs, new AddDownloadDraft("https://files.example.org/film.mp4", mime: "video/mp4"));

        await vm.AddCommand.ExecuteAsync(null);

        var request = jobs.Adds.Single().Request;
        request.CategoryId.Should().Be(5);
        request.Mime.Should().Be("video/mp4");
    }

    private static BrowserBatch Batch(params string[] names) => new(
        "https://files.example.org/page",
        [.. names.Select(n => new IpcLink { Url = "https://files.example.org/files/" + n })],
        [],
        "Agent/1.0");

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task The_picker_opens_on_by_file_type_and_leaves_each_link_to_the_rules()
    {
        var jobs = Jobs();
        var vm = new LinkPickerViewModel(Batch("a.zip", "b.mp4"), jobs);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.SelectedCategory.Should().BeSameAs(LinkPickerViewModel.AutomaticCategory);
        vm.Categories[0].Should().BeSameAs(LinkPickerViewModel.AutomaticCategory);
        foreach (var row in vm.Shown)
        {
            row.IsSelected = true;
        }

        await vm.AddCommand.ExecuteAsync(null);

        jobs.Adds.Should().HaveCount(2);
        jobs.Adds.Should().OnlyContain(a => a.Request.CategoryId == null);
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_category_named_in_the_picker_is_used_for_every_link()
    {
        var jobs = Jobs();
        var vm = new LinkPickerViewModel(Batch("a.zip", "b.mp4"), jobs);
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        vm.SelectedCategory = vm.Categories.Single(c => c.Name == "Code");
        foreach (var row in vm.Shown)
        {
            row.IsSelected = true;
        }

        await vm.AddCommand.ExecuteAsync(null);

        jobs.Adds.Should().OnlyContain(a => a.Request.CategoryId == 9);
    }
}
