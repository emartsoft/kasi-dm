namespace Kasi.Core.Tests;

[Trait("Requirement", "KASI-ENG-005")]
public class SegmentTests
{
    [Fact]
    public void A_new_segment_reports_its_length_cursor_and_completion()
    {
        var segment = new Segment(index: 2, start: 1000, end: 1500);

        segment.Length.Should().Be(500);
        segment.Remaining.Should().Be(500);
        segment.Cursor.Should().Be(1000);
        segment.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Advancing_moves_the_cursor_and_the_last_byte_completes_it()
    {
        var segment = new Segment(0, 1000, 1500).Advance(200);

        segment.Written.Should().Be(200);
        segment.Cursor.Should().Be(1200);
        segment.Advance(300).IsComplete.Should().BeTrue();
    }

    [Fact]
    public void Advance_does_not_change_the_original()
    {
        var original = new Segment(0, 0, 100);

        _ = original.Advance(10);

        original.Written.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, 0L, 10L, 0L)]   // negative index
    [InlineData(0, -1L, 10L, 0L)]   // negative start
    [InlineData(0, 10L, 10L, 0L)]   // empty range
    [InlineData(0, 10L, 5L, 0L)]    // inverted range
    [InlineData(0, 0L, 10L, -1L)]   // negative written
    [InlineData(0, 0L, 10L, 11L)]   // written beyond the range
    public void An_invalid_range_cannot_be_constructed(int index, long start, long end, long written)
    {
        var act = () => new Segment(index, start, end, written);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(101L)]
    public void Advance_rejects_negative_amounts_and_amounts_past_the_end(long bytes)
    {
        var act = () => new Segment(0, 0, 100).Advance(bytes);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void A_split_can_shorten_the_end_but_never_below_the_bytes_already_written()
    {
        var segment = new Segment(0, 0, 1000, written: 400);

        segment.WithEnd(700).End.Should().Be(700);
        segment.WithEnd(400).IsComplete.Should().BeTrue();
        var act = () => segment.WithEnd(399);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Offsets_beyond_four_GiB_are_supported()
    {
        const long fourGiB = 4L * 1024 * 1024 * 1024;

        var segment = new Segment(7, fourGiB, fourGiB + 2 * 1024 * 1024).Advance(1024 * 1024);

        segment.Cursor.Should().Be(fourGiB + 1024 * 1024);
        segment.Remaining.Should().Be(1024 * 1024);
    }
}
