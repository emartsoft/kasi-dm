using Kasi.Core;

namespace Kasi.Core.Tests;

/// <summary>KASI-CAT-002: the patterns of category rules.</summary>
[Trait("Requirement", "KASI-CAT-002")]
public class GlobPatternTests
{
    [Theory]
    [Trait("TC", "TC-CAT-002")]
    [InlineData("*.example.com", "dl.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.example.com", "a.b.example.com", true)]
    [InlineData("example.com", "EXAMPLE.com", true)]
    [InlineData("file?.zip", "file1.zip", true)]
    [InlineData("file?.zip", "file.zip", false)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("*", "anything", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    [InlineData("abc*", "abc", true)]
    [InlineData("https://example.org/releases/*", "https://example.org/releases/v2/app.bin", true)]
    public void It_matches_stars_and_question_marks_ignoring_case(string pattern, string text, bool expected)
    {
        GlobPattern.IsMatch(pattern, text).Should().Be(expected);
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void A_hostile_pattern_does_not_blow_up()
    {
        var pattern = string.Concat(Enumerable.Repeat("a*", 120)) + "b";
        var text = new string('a', 2_000);

        GlobPattern.IsMatch(pattern, text).Should().BeFalse("it finishes, and says no: there is no b");
    }

    [Theory]
    [Trait("TC", "TC-CAT-002")]
    [InlineData(CategoryRuleKind.Domain, "*.example.com", null)]
    [InlineData(CategoryRuleKind.Url, "https://example.org/*", null)]
    [InlineData(CategoryRuleKind.Extension, "mp4", null)]
    [InlineData(CategoryRuleKind.Extension, "tar.gz", null)]
    [InlineData(CategoryRuleKind.Domain, "", CategoryRulePatternError.Empty)]
    [InlineData(CategoryRuleKind.Domain, "   ", CategoryRulePatternError.Empty)]
    [InlineData(CategoryRuleKind.Domain, "*", CategoryRulePatternError.MatchesEverything)]
    [InlineData(CategoryRuleKind.Url, "***", CategoryRulePatternError.MatchesEverything)]
    [InlineData(CategoryRuleKind.Domain, "a b", CategoryRulePatternError.BadCharacters)]
    [InlineData(CategoryRuleKind.Extension, ".mp4", CategoryRulePatternError.BadExtension)]
    [InlineData(CategoryRuleKind.Extension, "mp4.", CategoryRulePatternError.BadExtension)]
    [InlineData(CategoryRuleKind.Extension, "a..b", CategoryRulePatternError.BadExtension)]
    [InlineData(CategoryRuleKind.Extension, "mp*", CategoryRulePatternError.BadExtension)]
    public void A_pattern_is_checked_before_it_is_saved(CategoryRuleKind kind, string pattern, CategoryRulePatternError? expected)
    {
        GlobPattern.Validate(kind, pattern).Should().Be(expected);
    }

    [Fact]
    [Trait("TC", "TC-CAT-002")]
    public void A_pattern_over_256_characters_is_refused()
    {
        GlobPattern.Validate(CategoryRuleKind.Domain, new string('a', 257)).Should().Be(CategoryRulePatternError.TooLong);
        GlobPattern.Validate(CategoryRuleKind.Domain, new string('a', 256)).Should().BeNull();
    }
}
