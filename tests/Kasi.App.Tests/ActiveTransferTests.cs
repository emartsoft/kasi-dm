using Kasi.App.ViewModels;
using Kasi.Core;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

/// <summary>UIR-3 (docs/plans/ui-redesign.md): the numbers behind the Active Transfer window. KASI-UI-003, TC-UI-002.</summary>
[Trait("Requirement", "KASI-UI-003")]
public class ActiveTransferTests
{
    private static async Task<(DetailsViewModel Details, EventingJobService Service, JobRowViewModel Row, FakeTimeProvider Clock)> OpenAsync(JobState state = JobState.Downloading)
    {
        var job = TestJobs.New("ubuntu.iso", state, total: 1_000_000, done: 642_000) with { Url = "https://files.example.org/ubuntu.iso?token=SECRET", Folder = @"D:\Downloads" };
        var service = new EventingJobService();
        service.Stored[job.Id] = job;
        var row = new JobRowViewModel(job);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        var details = new DetailsViewModel(row, service, new ShellSpy(), action => action(), clock);
        await details.StartAsync(TestContext.Current.CancellationToken);
        return (details, service, row, clock);
    }

    private static SegmentsProgressed Shape(JobId id, params (int Worker, long Written, long Speed)[] live) =>
        new(id, [.. live.Select(l => new Segment(l.Worker, l.Worker * 1000, (l.Worker * 1000) + 1000, l.Written))],
            [.. live.Select(l => new ConnectionView(l.Worker, l.Worker, l.Worker * 1000, (l.Worker * 1000) + 1000, l.Speed))]);

    [Fact]
    public async Task The_headline_is_the_percentage_with_one_decimal_and_the_protocol_comes_from_the_address()
    {
        var (details, _, _, _) = await OpenAsync();

        details.PercentText.Should().Be("64.2%");
        details.ProtocolText.Should().Be("HTTPS");
    }

    [Fact]
    public async Task The_chart_takes_one_sample_a_second_and_remembers_the_peak()
    {
        var (details, service, row, clock) = await OpenAsync();

        service.Raise(Shape(row.Id, (0, 0, 2_000_000), (1, 0, 1_000_000)));
        clock.Advance(TimeSpan.FromMilliseconds(250));
        service.Raise(Shape(row.Id, (0, 0, 9_000_000)));
        clock.Advance(TimeSpan.FromSeconds(1));
        service.Raise(Shape(row.Id, (0, 0, 1_000_000)));

        details.SpeedSamples.Should().Equal([3_000_000.0, 1_000_000.0], "the quick event inside the same second adds no sample");
        details.PeakText.Should().Be("2.9 MB/s");
    }

    [Fact]
    public async Task The_matrix_has_a_tile_per_connection_and_a_finished_connection_reads_Idle()
    {
        var (details, service, row, _) = await OpenAsync();

        service.Raise(Shape(row.Id, (0, 500, 1_000_000), (1, 250, 2_000_000)));

        details.Tiles.Select(t => t.Number).Should().Equal("#01", "#02");
        details.Tiles[0].PercentText.Should().Be("50%");
        details.Tiles[1].SpeedText.Should().Be("1.9 MB/s");
        details.MatrixTitle.Should().Be("Segment matrix (2 connections)");
        details.ActiveStreamsText.Should().Be("Active streams: 2/2");

        service.Raise(Shape(row.Id, (1, 500, 2_000_000)));

        details.Tiles.Should().HaveCount(2, "a connection that has gone stays as a tile");
        details.Tiles[0].IsIdle.Should().BeTrue();
        details.Tiles[0].SpeedText.Should().Be("Idle");
        details.Tiles[0].PercentText.Should().Be("50%", "it keeps what it had received");
        details.ActiveStreamsText.Should().Be("Active streams: 1/2");
        details.StatusLine.Should().Be("Downloading (1 streams active)");
    }

    [Fact]
    public async Task The_elapsed_clock_runs_while_the_job_transfers_and_stands_still_when_it_pauses()
    {
        var (details, service, row, clock) = await OpenAsync();

        clock.Advance(TimeSpan.FromSeconds(75));
        service.Raise(Shape(row.Id, (0, 0, 1)));
        details.ElapsedText.Should().Be("01:15");
        row.StateChanged(JobState.Paused);
        clock.Advance(TimeSpan.FromMinutes(10));
        service.Raise(Shape(row.Id, (0, 0, 1)));

        details.ElapsedText.Should().Be("01:15");
        row.StateChanged(JobState.Downloading);
        clock.Advance(TimeSpan.FromSeconds(5));
        service.Raise(Shape(row.Id, (0, 0, 1)));
        details.ElapsedText.Should().Be("01:20");
    }
}
