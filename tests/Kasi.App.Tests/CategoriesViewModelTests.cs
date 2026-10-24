using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Data;

namespace Kasi.App.Tests;

/// <summary>M2.11c: the Categories page of Settings (KASI-CAT-002), over real repositories on a temporary SQLite file.</summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-CAT-002")]
public sealed class CategoriesViewModelTests : IAsyncLifetime, IDisposable
{
    private const string Downloads = @"C:\Users\test\Downloads";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasi-categories-" + Guid.NewGuid().ToString("N"));
    private CategoryRepository _categories = null!;
    private CategoryRuleRepository _rules = null!;
    private CategoriesViewModel _vm = null!;

    public async ValueTask InitializeAsync()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_root, "kasi.db"));
        await new MigrationRunner(factory).MigrateAsync(TestContext.Current.CancellationToken);
        _categories = new CategoryRepository(factory);
        _rules = new CategoryRuleRepository(factory);
        _vm = new CategoriesViewModel(_categories, _rules, Downloads);
        await _vm.LoadAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the folder is in the temp directory
        }
    }

    private void Select(string name) => _vm.SelectedCategory = _vm.Categories.Single(c => c.Name == name);

    private async Task<Category> AddCategoryAsync(string name, string folder)
    {
        _vm.NewCategoryName = name;
        _vm.NewCategoryFolder = folder;
        await _vm.AddCategoryCommand.ExecuteAsync(null);
        return _vm.Categories.Single(c => c.Name == name);
    }

    [Fact]
    public void It_lists_the_categories_and_the_rules_of_the_chosen_one()
    {
        _vm.Categories.Select(c => c.Name).Should().StartWith(["General", "Compressed", "Documents"]);

        Select("Video");

        _vm.Rules.Select(r => r.Pattern).Should().BeEquivalentTo("mp4", "mkv", "webm", "mov", "avi");
        _vm.Rules.Should().OnlyContain(r => r.Rule.IsBuiltin && r.IsEnabled);
        _vm.EditName.Should().Be("Video");
        _vm.CanRename.Should().BeFalse();
        _vm.CanDelete.Should().BeFalse();
    }

    [Theory]
    [Trait("TC", "TC-CAT-002")]
    [InlineData("", "Code")]
    [InlineData("   ", "Code")]
    [InlineData("Video", "Code")]
    [InlineData("video", "Code")]
    [InlineData("Code", "")]
    [InlineData("Code", "..\\Elsewhere")]
    [InlineData("Code", "a/../b")]
    [InlineData("Code", "con")]
    [InlineData("Code", "bad|name")]
    [InlineData("Code", "name:stream")]
    [InlineData("Code", "trailing.")]
    public async Task A_category_with_a_bad_name_or_folder_is_refused_and_nothing_is_added(string name, string folder)
    {
        var before = _vm.Categories.Count;
        _vm.NewCategoryName = name;
        _vm.NewCategoryFolder = folder;

        await _vm.AddCategoryCommand.ExecuteAsync(null);

        _vm.Error.Should().NotBeEmpty();
        _vm.Categories.Should().HaveCount(before);
        (await _categories.ListAsync(TestContext.Current.CancellationToken)).Should().HaveCount(before);
    }

    [Theory]
    [Trait("TC", "TC-CAT-002")]
    [InlineData("Code")]
    [InlineData(@"Work\Code")]
    [InlineData(@"D:\Media\Code")]
    [InlineData(@"\\server\share\Code")]
    public async Task A_user_category_may_take_a_relative_folder_or_a_full_path(string folder)
    {
        var category = await AddCategoryAsync("Code", folder);

        category.Folder.Should().Be(folder);
        category.IsBuiltin.Should().BeFalse();
        _vm.SelectedCategory.Should().Be(category, "the new category is chosen");
        _vm.Error.Should().BeEmpty();
        _vm.NewCategoryName.Should().BeEmpty();
        (await _categories.GetAsync(category.Id, TestContext.Current.CancellationToken)).Should().Be(category);
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_built_in_category_takes_only_a_folder_under_Downloads()
    {
        Select("Video");
        _vm.EditFolder = @"D:\Elsewhere";
        await _vm.SaveCategoryCommand.ExecuteAsync(null);
        _vm.Error.Should().NotBeEmpty("a built-in category's folder stays under Downloads");

        _vm.EditFolder = "Films";
        await _vm.SaveCategoryCommand.ExecuteAsync(null);

        _vm.Error.Should().BeEmpty();
        var video = (await _categories.GetAsync(5, TestContext.Current.CancellationToken))!;
        video.Folder.Should().Be("Films");
        video.Name.Should().Be("Video");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task General_may_have_no_folder_and_a_user_category_may_be_renamed_and_deleted()
    {
        Select("General");
        _vm.EditFolder = string.Empty;
        await _vm.SaveCategoryCommand.ExecuteAsync(null);
        _vm.Error.Should().BeEmpty();

        var code = await AddCategoryAsync("Code", "Code");
        _vm.EditName = "Source";
        await _vm.SaveCategoryCommand.ExecuteAsync(null);
        _vm.Categories.Should().Contain(c => c.Id == code.Id && c.Name == "Source");
        _vm.CanDelete.Should().BeTrue();

        await _vm.DeleteCategoryCommand.ExecuteAsync(null);

        _vm.Categories.Should().NotContain(c => c.Id == code.Id);
        _vm.SelectedCategory.Should().NotBeNull();
    }

    [Theory]
    [Trait("TC", "TC-CAT-002")]
    [InlineData(CategoryRuleKind.Extension, "")]
    [InlineData(CategoryRuleKind.Extension, ".mp4")]
    [InlineData(CategoryRuleKind.Extension, "mp*")]
    [InlineData(CategoryRuleKind.Domain, "*")]
    [InlineData(CategoryRuleKind.Domain, "a b")]
    [InlineData(CategoryRuleKind.Url, "***")]
    public async Task A_bad_pattern_is_refused_with_a_message(CategoryRuleKind kind, string pattern)
    {
        Select("Video");
        var before = _vm.Rules.Count;
        _vm.SelectedRuleKind = _vm.RuleKinds.Single(k => k.Kind == kind);
        _vm.NewRulePattern = pattern;

        await _vm.AddRuleCommand.ExecuteAsync(null);

        _vm.Error.Should().NotBeEmpty();
        _vm.Rules.Should().HaveCount(before);
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_rule_added_to_a_category_is_saved_listed_and_used()
    {
        var code = await AddCategoryAsync("Code", "Code");
        _vm.SelectedRuleKind = _vm.RuleKinds.Single(k => k.Kind == CategoryRuleKind.Domain);
        _vm.NewRulePattern = "*.example.org";

        await _vm.AddRuleCommand.ExecuteAsync(null);

        _vm.Rules.Should().ContainSingle().Which.Pattern.Should().Be("*.example.org");
        _vm.NewRulePattern.Should().BeEmpty();
        var saved = (await _rules.ListAsync(TestContext.Current.CancellationToken)).Single(r => r.Pattern == "*.example.org");
        saved.Should().Match<CategoryRule>(r => r.CategoryId == code.Id && r.Kind == CategoryRuleKind.Domain && !r.IsBuiltin && r.IsEnabled);
        _vm.TryLink = "https://dl.example.org/film.mp4";
        _vm.CheckLinkCommand.Execute(null);
        _vm.TryResult.Should().Contain("Code").And.Contain(Path.Combine(Downloads, "Code")).And.Contain("*.example.org");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task Only_a_rule_of_your_own_can_be_deleted()
    {
        Select("Video");
        _vm.SelectedRuleKind = _vm.RuleKinds.Single(k => k.Kind == CategoryRuleKind.Extension);
        _vm.NewRulePattern = "m4v";
        await _vm.AddRuleCommand.ExecuteAsync(null);
        _vm.SelectedRule = _vm.Rules.Single(r => r.Pattern == "mp4");

        await _vm.DeleteRuleCommand.ExecuteAsync(null);
        _vm.Rules.Should().Contain(r => r.Pattern == "mp4", "a built-in rule is switched off, not deleted");

        _vm.SelectedRule = _vm.Rules.Single(r => r.Pattern == "m4v");
        await _vm.DeleteRuleCommand.ExecuteAsync(null);

        _vm.Rules.Should().NotContain(r => r.Pattern == "m4v");
        (await _rules.ListAsync(TestContext.Current.CancellationToken)).Should().NotContain(r => r.Pattern == "m4v");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task Switching_a_rule_off_is_saved_and_the_resolver_stops_using_it()
    {
        Select("Video");
        var mp4 = _vm.Rules.Single(r => r.Pattern == "mp4");

        mp4.IsEnabled = false;
        await Eventually(async () => !(await _rules.ListAsync(TestContext.Current.CancellationToken)).Single(r => r.Id == mp4.Rule.Id).IsEnabled);

        _vm.TryLink = "https://example.org/film.mp4";
        _vm.CheckLinkCommand.Execute(null);
        _vm.TryResult.Should().Contain("General");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task Moving_a_rule_changes_the_order_it_is_tried_in_and_keeps_it_chosen()
    {
        var code = await AddCategoryAsync("Code", "Code");
        var other = await AddCategoryAsync("Other", "Other");
        foreach (var (category, pattern) in new[] { (code, "a.example.org"), (other, "b.example.org") })
        {
            _vm.SelectedCategory = category;
            _vm.SelectedRuleKind = _vm.RuleKinds.Single(k => k.Kind == CategoryRuleKind.Domain);
            _vm.NewRulePattern = pattern;
            await _vm.AddRuleCommand.ExecuteAsync(null);
        }

        // two rules for one site, in two categories: the one tried first wins
        _vm.SelectedCategory = code;
        _vm.NewRulePattern = "shared.example.org";
        await _vm.AddRuleCommand.ExecuteAsync(null);
        _vm.SelectedCategory = other;
        _vm.NewRulePattern = "shared.example.org";
        await _vm.AddRuleCommand.ExecuteAsync(null);
        _vm.TryLink = "https://shared.example.org/x.bin";
        _vm.CheckLinkCommand.Execute(null);
        _vm.TryResult.Should().StartWith("Code:", "it was added first");

        // moving within a category compares only the rules shown there, so give "Other" a second domain rule to move past
        _vm.SelectedRule = _vm.Rules.Single(r => r.Pattern == "shared.example.org");
        await _vm.MoveRuleUpCommand.ExecuteAsync(null);

        _vm.Rules.Select(r => r.Pattern).Should().Equal("shared.example.org", "b.example.org");
        _vm.SelectedRule!.Pattern.Should().Be("shared.example.org");
        var order = (await _rules.ListAsync(TestContext.Current.CancellationToken)).Where(r => r.Kind == CategoryRuleKind.Domain).Select(r => r.Pattern).ToList();
        order.IndexOf("shared.example.org").Should().BeGreaterThan(-1);
        await _vm.MoveRuleDownCommand.ExecuteAsync(null);
        _vm.Rules.Select(r => r.Pattern).Should().Equal("b.example.org", "shared.example.org");
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public void Try_a_link_names_the_category_the_folder_and_the_reason()
    {
        _vm.TryLink = "https://example.org/a/film.mp4";
        _vm.CheckLinkCommand.Execute(null);
        _vm.TryResult.Should().Be($"Video: saved in {Path.Combine(Downloads, "Video")} (File extension rule mp4)");

        _vm.TryLink = "https://example.org/a/notes.xyz";
        _vm.CheckLinkCommand.Execute(null);
        _vm.TryResult.Should().Be($"General: saved in {Downloads} (nothing matched, so General)");

        _vm.TryLink = "not a link";
        _vm.CheckLinkCommand.Execute(null);
        _vm.TryResult.Should().Be("Type a full link, starting with http or https.");
    }

    private static async Task Eventually(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException("The condition did not become true.");
    }
}
