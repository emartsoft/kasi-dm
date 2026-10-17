namespace Kasi.Core.Tests;

/// <summary>The state and kind strings are what is stored in SQLite, so they are pinned here.</summary>
[Trait("Requirement", "TRD-6.9")]
public class JobTextTests
{
    private static readonly string[] _stateNames =
        ["Queued", "Probing", "Downloading", "Retrying", "Paused", "Muxing", "Verifying", "Completed", "Failed"];

    public static TheoryData<JobState> AllStates => new(Enum.GetValues<JobState>());

    public static TheoryData<JobKind> AllKinds => new(Enum.GetValues<JobKind>());

    [Fact]
    public void There_are_exactly_the_nine_states_of_the_state_diagram()
    {
        Enum.GetNames<JobState>().Should().Equal(_stateNames);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void State_text_is_the_state_name_and_round_trips(JobState state)
    {
        var text = JobStateText.ToText(state);

        text.Should().Be(state.ToString());
        JobStateText.TryParse(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(state);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("queued")]
    [InlineData("QUEUED")]
    [InlineData(" Queued")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Seeding")]
    public void Unknown_state_text_is_rejected(string? text)
    {
        JobStateText.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void An_undefined_state_cannot_be_written()
    {
        var act = () => JobStateText.ToText((JobState)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Kind_text_is_the_lower_case_name_and_round_trips(JobKind kind)
    {
        var text = JobKindText.ToText(kind);

        text.Should().Be(kind.ToString().ToLowerInvariant());
        JobKindText.TryParse(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Http")]
    [InlineData("0")]
    [InlineData("magnet")]
    public void Unknown_kind_text_is_rejected(string? text)
    {
        JobKindText.TryParse(text, out _).Should().BeFalse();
    }
}
