using Kasi.Core;

namespace Kasi.Data.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-CAT-001")]
public sealed class CategoryRuleRepositoryTests : IAsyncLifetime, IDisposable
{
    private readonly TempDatabase _db = new();
    private CategoryRuleRepository _rules = null!;

    public async ValueTask InitializeAsync()
    {
        await new MigrationRunner(_db.Factory).MigrateAsync(TempDatabase.Ct);
        _rules = new CategoryRuleRepository(_db.Factory);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose() => _db.Dispose();

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task The_default_extension_rules_are_seeded_for_the_built_in_categories()
    {
        var rules = await _rules.ListAsync(TempDatabase.Ct);

        rules.Should().OnlyContain(r => r.IsBuiltin && r.IsEnabled && r.Kind == CategoryRuleKind.Extension);
        var byCategory = rules.GroupBy(r => r.CategoryId).ToDictionary(g => g.Key, g => g.Select(r => r.Pattern).ToList());
        byCategory[5].Should().BeEquivalentTo("mp4", "mkv", "webm", "mov", "avi");
        byCategory[2].Should().Contain(["zip", "rar", "7z", "tar", "gz", "xz", "bz2", "tar.gz"]);
        byCategory[7].Should().Contain(["jpg", "png", "webp", "gif", "svg"]);
        byCategory.Should().NotContainKey(1, "General is the fallback and has no rules");
        byCategory.Should().NotContainKey(8, "Torrents is routed by job kind");
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public async Task The_seeded_rules_route_the_files_of_the_TRD_table_end_to_end()
    {
        var categories = await new CategoryRepository(_db.Factory).ListAsync(TempDatabase.Ct);
        var rules = await _rules.ListAsync(TempDatabase.Ct);

        string Of(string file) => CategoryResolver.Resolve(new Uri("https://example.org/" + file), null, null, rules, categories)!.Category.Name;

        Of("a.mp4").Should().Be("Video");
        Of("a.pdf").Should().Be("Documents");
        Of("a.flac").Should().Be("Music");
        Of("a.msix").Should().Be("Programs");
        Of("a.webp").Should().Be("Images");
        Of("a.xz").Should().Be("Compressed");
        Of("a.unknown").Should().Be("General");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_user_rule_round_trips_and_is_listed_in_position_order()
    {
        var late = await _rules.AddAsync(new CategoryRule { CategoryId = 3, Kind = CategoryRuleKind.Domain, Pattern = "*.example.com", Position = 900 }, TempDatabase.Ct);
        var early = await _rules.AddAsync(new CategoryRule { CategoryId = 4, Kind = CategoryRuleKind.Url, Pattern = "https://x.org/*", Position = 1, IsBuiltin = true }, TempDatabase.Ct);

        var rules = await _rules.ListAsync(TempDatabase.Ct);

        rules.Select(r => r.Id).Should().ContainInOrder(early, late);
        var saved = rules.Single(r => r.Id == late);
        saved.Should().Be(new CategoryRule { Id = late, CategoryId = 3, Kind = CategoryRuleKind.Domain, Pattern = "*.example.com", Position = 900, IsBuiltin = false, IsEnabled = true });
        rules.Single(r => r.Id == early).IsBuiltin.Should().BeFalse("a rule added through the repository is always the user's");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public async Task A_rule_can_be_changed_and_a_built_in_rule_can_be_switched_off_but_not_deleted()
    {
        var id = await _rules.AddAsync(new CategoryRule { CategoryId = 3, Kind = CategoryRuleKind.Domain, Pattern = "a.org", Position = 5 }, TempDatabase.Ct);
        var builtin = (await _rules.ListAsync(TempDatabase.Ct)).First(r => r.IsBuiltin);

        (await _rules.UpdateAsync((await _rules.ListAsync(TempDatabase.Ct)).Single(r => r.Id == id) with { CategoryId = 5, Pattern = "b.org", Position = 7, IsEnabled = false }, TempDatabase.Ct)).Should().BeTrue();
        (await _rules.UpdateAsync(builtin with { IsEnabled = false }, TempDatabase.Ct)).Should().BeTrue();

        var rules = await _rules.ListAsync(TempDatabase.Ct);
        rules.Single(r => r.Id == id).Should().BeEquivalentTo(new CategoryRule { Id = id, CategoryId = 5, Kind = CategoryRuleKind.Domain, Pattern = "b.org", Position = 7, IsEnabled = false });
        rules.Single(r => r.Id == builtin.Id).IsEnabled.Should().BeFalse();
        (await _rules.DeleteAsync(builtin.Id, TempDatabase.Ct)).Should().BeFalse("built-in rules are switched off, not deleted");
        (await _rules.DeleteAsync(id, TempDatabase.Ct)).Should().BeTrue();
        (await _rules.DeleteAsync(id, TempDatabase.Ct)).Should().BeFalse();
        (await _rules.UpdateAsync(new CategoryRule { Id = 99999, CategoryId = 1, Kind = CategoryRuleKind.Domain, Pattern = "x" }, TempDatabase.Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_a_category_deletes_its_rules()
    {
        var categoryId = 50;
        await _db.ExecAsync($"INSERT INTO category (id, name, folder, is_builtin) VALUES ({categoryId}, 'Code', 'Code', 0);");
        await _rules.AddAsync(new CategoryRule { CategoryId = categoryId, Kind = CategoryRuleKind.Domain, Pattern = "github.com" }, TempDatabase.Ct);

        await _db.ExecAsync($"DELETE FROM category WHERE id = {categoryId};");

        (await _rules.ListAsync(TempDatabase.Ct)).Should().NotContain(r => r.CategoryId == categoryId);
    }
}
