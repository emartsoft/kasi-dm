using Kasi.Core;

namespace Kasi.Core.Tests;

/// <summary>KASI-CAT-001 and 002: the category a download gets, in the order of TRD 10.4.</summary>
[Trait("Requirement", "KASI-CAT-001")]
public class CategoryResolverTests
{
    private static readonly IReadOnlyList<Category> _categories =
    [
        new() { Id = 1, Name = "General", Folder = string.Empty, IsBuiltin = true },
        new() { Id = 2, Name = "Compressed", Folder = "Compressed", IsBuiltin = true },
        new() { Id = 3, Name = "Documents", Folder = "Documents", IsBuiltin = true },
        new() { Id = 4, Name = "Music", Folder = "Music", IsBuiltin = true },
        new() { Id = 5, Name = "Video", Folder = "Video", IsBuiltin = true },
        new() { Id = 6, Name = "Programs", Folder = "Programs", IsBuiltin = true },
        new() { Id = 7, Name = "Images", Folder = "Images", IsBuiltin = true },
        new() { Id = 9, Name = "Code", Folder = "Code" },
    ];

    private static int _nextId = 100;

    private static CategoryRule Extension(int category, string pattern, int position = 0, bool builtin = true) =>
        new() { Id = _nextId++, CategoryId = category, Kind = CategoryRuleKind.Extension, Pattern = pattern, Position = position, IsBuiltin = builtin };

    private static CategoryRule Domain(int category, string pattern, int position = 0) =>
        new() { Id = _nextId++, CategoryId = category, Kind = CategoryRuleKind.Domain, Pattern = pattern, Position = position };

    private static CategoryRule Address(int category, string pattern, int position = 0) =>
        new() { Id = _nextId++, CategoryId = category, Kind = CategoryRuleKind.Url, Pattern = pattern, Position = position };

    private static readonly IReadOnlyList<CategoryRule> _defaults =
    [
        Extension(2, "zip"), Extension(2, "gz"), Extension(2, "tar.gz"), Extension(3, "pdf"), Extension(4, "mp3"), Extension(5, "mp4"),
        Extension(5, "mkv"), Extension(6, "exe"), Extension(6, "iso"), Extension(7, "png"),
    ];

    private static string Name(CategoryMatch? match) => match?.Category.Name ?? "(none)";

    private static CategoryMatch? Resolve(string url, IReadOnlyList<CategoryRule> rules, string? fileName = null, string? mime = null) =>
        CategoryResolver.Resolve(new Uri(url), fileName, mime, rules, _categories);

    [Theory]
    [Trait("TC", "TC-CAT-001")]
    [InlineData("https://example.org/a/film.mp4", "Video")]
    [InlineData("https://example.org/a/film.MKV", "Video")]
    [InlineData("https://example.org/a/song.mp3", "Music")]
    [InlineData("https://example.org/a/report.pdf", "Documents")]
    [InlineData("https://example.org/a/setup.exe", "Programs")]
    [InlineData("https://example.org/a/disc.iso", "Programs")]
    [InlineData("https://example.org/a/photo.png", "Images")]
    [InlineData("https://example.org/a/backup.zip", "Compressed")]
    [InlineData("https://example.org/a/backup.tar.gz", "Compressed")]
    [InlineData("https://example.org/a/notes.xyz", "General")]
    [InlineData("https://example.org/a/README", "General")]
    public void A_default_extension_rule_routes_the_file_to_its_category(string url, string expected)
    {
        Name(Resolve(url, _defaults)).Should().Be(expected);
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public void The_file_name_wins_over_the_address_and_the_query_is_ignored()
    {
        Name(Resolve("https://example.org/download?id=7&name=a.zip", _defaults, fileName: "film.mp4")).Should().Be("Video");
        Name(Resolve("https://example.org/get/file.zip?token=abc.mp4", _defaults)).Should().Be("Compressed");
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public void A_double_extension_is_tried_before_the_last_part()
    {
        var rules = new[] { Extension(2, "gz", position: 1), Extension(3, "tar.gz", position: 2) };

        Name(Resolve("https://example.org/x.tar.gz", rules)).Should().Be("Documents", "tar.gz is more specific than gz, whatever the positions");
        Name(Resolve("https://example.org/x.gz", rules)).Should().Be("Compressed");
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public void A_leading_dot_is_a_name_and_not_an_extension()
    {
        var rules = new[] { Extension(3, "gitignore") };

        Name(Resolve("https://example.org/.gitignore", rules)).Should().Be("General");
    }

    [Theory]
    [Trait("TC", "TC-CAT-001")]
    [InlineData("video/mp4", "Video")]
    [InlineData("audio/mpeg", "Music")]
    [InlineData("image/png", "Images")]
    [InlineData("application/pdf", "Documents")]
    [InlineData("application/zip; charset=binary", "Compressed")]
    [InlineData("application/x-msdownload", "Programs")]
    [InlineData("application/octet-stream", "General")]
    [InlineData("", "General")]
    public void The_MIME_type_decides_only_when_the_name_has_no_extension(string mime, string expected)
    {
        Name(Resolve("https://example.org/download", _defaults, mime: mime)).Should().Be(expected);
    }

    [Fact]
    [Trait("TC", "TC-CAT-001")]
    public void The_MIME_type_is_ignored_when_the_name_has_an_extension()
    {
        Name(Resolve("https://example.org/data.xyz", _defaults, mime: "video/mp4")).Should().Be("General");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void A_domain_rule_beats_the_extension_rule()
    {
        var rules = new List<CategoryRule>(_defaults) { Domain(9, "*.example.com") };

        var match = Resolve("https://dl.example.com/tool.zip", rules);

        Name(match).Should().Be("Code");
        match!.Rule!.Kind.Should().Be(CategoryRuleKind.Domain);
        Name(Resolve("https://other.org/tool.zip", rules)).Should().Be("Compressed", "the rule is for example.com only");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void A_wildcard_domain_rule_does_not_match_the_bare_domain()
    {
        var rules = new[] { Domain(9, "*.github.com"), Domain(3, "github.com") };

        Name(Resolve("https://raw.github.com/a.bin", rules)).Should().Be("Code");
        Name(Resolve("https://github.com/a.bin", rules)).Should().Be("Documents", "a person who wants both writes two rules");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void An_address_rule_matches_the_address_without_its_query()
    {
        var rules = new[] { Address(9, "https://example.org/releases/*") };

        Name(Resolve("https://example.org/releases/v2/app.bin?token=x", rules)).Should().Be("Code");
        Name(Resolve("https://example.org/other/app.bin?path=/releases/", rules)).Should().Be("General", "the query is not part of the address");
        Name(Resolve("https://example.org/other/app.bin?path=/releases/", [Address(9, "*releases*")])).Should().Be("General", "not even a pattern that would find it there");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void Two_user_rules_are_tried_in_position_order_and_the_first_match_wins()
    {
        var late = Domain(3, "example.org", position: 5);
        var early = Domain(9, "example.org", position: 1);

        Name(Resolve("https://example.org/a.bin", [late, early])).Should().Be("Code");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void A_user_extension_rule_beats_a_built_in_one()
    {
        var rules = new[] { Extension(2, "zip", position: 1, builtin: true), Extension(9, "zip", position: 99, builtin: false) };

        Name(Resolve("https://example.org/a.zip", rules)).Should().Be("Code");
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void A_disabled_rule_and_a_rule_for_a_missing_category_are_skipped()
    {
        var off = Domain(9, "example.org") with { IsEnabled = false };
        var orphan = Domain(42, "example.org");

        Name(Resolve("https://example.org/a.mp4", [off, orphan, .. _defaults])).Should().Be("Video");
    }

    [Fact]
    public void With_no_categories_there_is_no_match_and_without_General_the_first_category_is_used()
    {
        CategoryResolver.Resolve(new Uri("https://example.org/a.bin"), null, null, [], []).Should().BeNull();
        CategoryResolver.Resolve(new Uri("https://example.org/a.bin"), null, null, [], [_categories[1]])!.Category.Name.Should().Be("Compressed");
    }

    [Fact]
    public void The_match_names_the_rule_that_decided()
    {
        var rule = Extension(5, "mp4");

        var match = Resolve("https://example.org/a.mp4", [rule]);

        match!.Rule.Should().Be(rule);
        Resolve("https://example.org/a.xyz", [rule])!.Rule.Should().BeNull("General was the fallback, not a rule");
    }
}
