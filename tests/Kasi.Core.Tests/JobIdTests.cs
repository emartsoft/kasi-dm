namespace Kasi.Core.Tests;

[Trait("Requirement", "KASI-RES-006")]
public class JobIdTests
{
    private static readonly DateTimeOffset _start = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void New_ids_are_version_7_and_sort_by_creation_time()
    {
        var clock = new ManualTimeProvider(_start);
        var ids = new List<JobId>();
        for (var i = 0; i < 20; i++)
        {
            ids.Add(JobId.New(clock));
            clock.Advance(TimeSpan.FromMilliseconds(5));
        }

        ids.Should().OnlyContain(id => id.Value.Version == 7);
        ids.Select(id => id.ToString()).Should().BeInAscendingOrder(StringComparer.Ordinal);
        ids.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Text_is_lower_case_36_characters_and_round_trips()
    {
        var id = JobId.New(new ManualTimeProvider(_start));

        var text = id.ToString();

        text.Should().HaveLength(36).And.Be(text.ToLowerInvariant());
        JobId.TryParse(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("0194b8c0e6a07e2c8d9a1f3b5c7d9e0f")]       // no hyphens
    [InlineData("{0194b8c0-e6a0-7e2c-8d9a-1f3b5c7d9e0f}")] // braces
    [InlineData("a3bb189e-8bf9-4888-9912-ace4e6543002")]   // a valid UUID, but version 4
    public void Anything_but_a_version_7_uuid_in_the_36_character_form_is_rejected(string? text)
    {
        JobId.TryParse(text, out _).Should().BeFalse();
    }
}
