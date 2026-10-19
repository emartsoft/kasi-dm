using System.Diagnostics;
using System.Security.Cryptography;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;

namespace Kasi.Engine.Tests;

[Collection(BandwidthRateGroup.Name)]
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-005")]
[Trait("Requirement", "KASI-ENG-004")]
public sealed class HttpTransferLiveSchedulerTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-live-scheduler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        finally
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    [Trait("TC", "TC-ENG-008")]
    [Trait("TC", "TC-ENG-009")]
    public async Task One_of_eight_connections_at_50_KB_per_s_finishes_through_endgame_duplication_without_aborting_a_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "live-endgame";
        const int size = 2 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            ThrottleConnectionIndex = 0,
            ThrottleBytesPerSecond = 50 * 1024,
        });
        var options = Options(8) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
        };
        var timer = Stopwatch.StartNew();

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            options,
            ct);
        timer.Stop();

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.EndgameDuplicateBytes.Should().BeGreaterThan(0);
        completed.EndgameDuplicateBytes.Should().BeLessThanOrEqualTo(2 * 1024 * 1024);
        completed.FinalSegments.Should().OnlyContain(s => s.IsComplete);
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
        _host.Metrics.AbortedRequests.Should().Be(0);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the slow probe lease must not stall the finished download");
    }

    [Fact]
    [Trait("TC", "TC-ENG-008")]
    public async Task A_persistently_slow_connection_does_not_hold_the_download_back_past_the_ten_second_window()
    {
        // The takeover rule itself (carve after 10 s below 30% of the median) is proven in the scheduler simulation tests;
        // this live run proves its effect end to end: the aggregate rate stays near what the seven healthy connections can
        // deliver (about 7 MB/s) instead of collapsing onto the 50 KB/s connection, so the file finishes in a bounded time
        // (with the takeover disabled the same run takes over a minute, so this test does notice a regression). The bounds are
        // relative to the rate this run reaches before the window opens, so a slow or busy runner scales them instead of
        // failing them.
        var ct = TestContext.Current.CancellationToken;
        const string seed = "live-slow-lane";
        const int size = 128 * 1024 * 1024;
        var updates = new List<(double Seconds, long Written)>();
        var gate = new Lock();
        var timer = Stopwatch.StartNew();
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            ThrottleConnectionIndex = 0,
            ThrottleBytesPerSecond = 50 * 1024,
            OtherConnectionsBytesPerSecond = 1024 * 1024,
        });
        var options = Options(8) with
        {
            RequestHeaders = new Dictionary<string, string> { [ChaosRegistry.HeaderName] = chaosId },
            SchedulerTick = scheduler =>
            {
                lock (gate)
                {
                    updates.Add((timer.Elapsed.TotalSeconds, scheduler.TotalWritten));
                }
            },
        };

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            options,
            ct);
        timer.Stop();

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
        _host.Metrics.AbortedRequests.Should().Be(0);
        timer.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(10), "the run must last past the 10 s slow-lane window");

        (double Seconds, long Written)[] samples;
        lock (gate)
        {
            samples = [.. updates];
        }

        double Rate(double from, double to)
        {
            var window = samples.Where(sample => sample.Seconds >= from && sample.Seconds <= to).ToArray();
            window.Length.Should().BeGreaterThan(4, $"the period {from}-{to} s must be sampled");
            return (window[^1].Written - window[0].Written) / (window[^1].Seconds - window[0].Seconds);
        }

        var elapsed = timer.Elapsed.TotalSeconds;
        var before = Rate(3, 9); // all connections up, the slow lane not yet carved: about 7 MB/s on a developer machine
        var after = Rate(10, elapsed - 2);
        after.Should().BeGreaterThan(before * 0.5, "after the window the healthy connections keep carrying the download");
        elapsed.Should().BeLessThan(size / before * 2 + 10, "the run takes about size over the healthy rate; a lane stuck at 50 KB/s would take several times that");
    }

    [Fact]
    [Trait("TC", "TC-ENG-011")]
    public async Task Splits_happen_while_leases_are_in_flight_without_aborted_requests()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "live-splits";
        const int size = 32 * 1024 * 1024;

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(8),
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.StructureVersion.Should().BeGreaterThan(0);
        completed.FinalSegments.Should().OnlyContain(s => s.IsComplete);
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
        _host.Metrics.AbortedRequests.Should().Be(0);
    }

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private static HttpTransferOptions Options(int initialConnections) => new()
    {
        SchedulerOptions = new SegmentSchedulerOptions
        {
            MaxConnections = initialConnections,
            InitialConnections = initialConnections,
        },
    };

    private static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }
}
