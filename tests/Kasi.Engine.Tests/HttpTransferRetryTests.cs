using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-RES-004")]
public sealed class HttpTransferRetryTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-retry-" + Guid.NewGuid().ToString("N"));
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
    [Trait("TC", "TC-RES-007")]
    public async Task A_short_503_burst_after_the_probe_recovers_and_the_file_verifies()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-503-burst";
        const int size = 8 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            StatusCode = 503,
            StatusAfterRequests = 1,
            StatusCount = 2,
        });

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(4) with { RequestHeaders = ChaosHeader(chaosId) },
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    [Trait("TC", "TC-RES-008")]
    public async Task A_429_with_Retry_After_waits_and_reduces_the_scheduler_connection_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-429";
        const int size = 32 * 1024 * 1024;
        var allowed = new ConcurrentBag<int>();
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            StatusCode = 429,
            StatusAfterRequests = 0,
            StatusCount = 1,
            RetryAfterSeconds = 5,
        });
        var timer = Stopwatch.StartNew();

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(4) with
            {
                RequestHeaders = ChaosHeader(chaosId),
                SchedulerTick = scheduler => allowed.Add(scheduler.AllowedConnections),
            },
            ct);
        timer.Stop();

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        timer.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(5));
        allowed.Should().NotBeEmpty();
        allowed.Min().Should().BeLessThanOrEqualTo(3);
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    [Trait("TC", "TC-RES-009")]
    public async Task A_404_on_the_first_lease_fails_without_a_retry_loop()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-404";
        const int size = 8 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            StatusCode = 404,
            StatusAfterRequests = 1,
            StatusCount = 1,
        });

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(4) with { RequestHeaders = ChaosHeader(chaosId) },
            ct);

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.HttpStatusError);
        failed.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
        _host.Metrics.Http1RequestsServed.Should().BeLessThanOrEqualTo(5, "the probe plus at most one request per worker");
    }

    [Fact]
    [Trait("TC", "TC-RES-012")]
    public async Task A_truncated_lease_is_retried_and_the_file_verifies()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-truncated";
        const int size = 8 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            TruncateAfterBytes = 256 * 1024,
            TruncateOnRequest = 1,
        });

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(4) with { RequestHeaders = ChaosHeader(chaosId) },
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task A_single_connection_reset_is_retried_and_the_file_verifies()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-reset";
        const int size = 8 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            ResetAfterBytes = 256 * 1024,
            ResetOnRequest = 1,
        });

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(4) with { RequestHeaders = ChaosHeader(chaosId) },
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        _host.Metrics.AbortedRequests.Should().Be(0);
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    [Trait("TC", "TC-RES-007")]
    public async Task A_connection_that_dies_every_128_KiB_still_completes_because_progress_resets_the_retry_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-progress";
        const int size = 4 * 1024 * 1024; // 32 resets on one connection: more than the ten attempts, so only progress can save it
        var chaosId = _host.Chaos.Register(new ChaosProfile { ResetAfterBytes = 128 * 1024 });

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(1) with { RequestHeaders = ChaosHeader(chaosId), RetryPolicy = FastRetry },
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.RetryCount.Should().BeGreaterThan(10);
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task A_stalled_connection_is_abandoned_after_the_read_stall_timeout_and_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-stall";
        const int size = 4 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile { StallAfterBytes = 128 * 1024, StallSeconds = 10, StallOnRequest = 1 });
        var timer = Stopwatch.StartNew();

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(4) with
            {
                RequestHeaders = ChaosHeader(chaosId),
                RetryPolicy = FastRetry with { ReadStallTimeout = TimeSpan.FromMilliseconds(500) },
            },
            ct);
        timer.Stop();

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.RetryCount.Should().BeGreaterThan(0);
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8), "each stall lasts 10 s, so finishing sooner means the timeout abandoned the lease");
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    [Trait("TC", "TC-RES-008")]
    public async Task A_throttled_lease_pauses_every_worker_for_the_Retry_After_window_and_reduces_the_pool_once()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-429-lease";
        const int size = 12 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            ThrottleBytesPerSecond = 1024 * 1024, // each connection: a 512 KiB lease takes about half a second, so workers keep asking for more
            StatusCode = 429,
            StatusAfterRequests = 1, // the probe is request 0; the next three lease requests (three workers at once) are throttled
            StatusCount = 3,
            RetryAfterSeconds = 2,
        });

        var download = new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(4) with { RequestHeaders = ChaosHeader(chaosId), RetryPolicy = FastRetry },
            ct);
        await EventuallyAsync(() => _host.Metrics.Http1RequestsServed >= 2);
        await Task.Delay(800, ct); // the leases already in flight finish and ask for their next one
        var before = _host.Metrics.Http1RequestsServed;
        await Task.Delay(1000, ct);
        var during = _host.Metrics.Http1RequestsServed;
        var outcome = await download;

        during.Should().Be(before, "every worker must wait out the Retry-After window, not only the one that received the 429");
        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.FinalAllowedConnections.Should().Be(3, "one throttle window lowers the pool by exactly one");
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    [Trait("TC", "TC-RES-007")]
    public async Task Thirty_percent_503s_and_a_reset_every_5_MiB_recover_without_user_action()
    {
        var ct = TestContext.Current.CancellationToken;
        const string seed = "retry-tpl";
        const int size = 32 * 1024 * 1024;
        var chaosId = _host.Chaos.Register(new ChaosProfile
        {
            Seed = 42,
            StatusCode = 503,
            StatusRate = 0.30,
            ResetAfterBytes = 5 * 1024 * 1024,
        });

        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            Options(8) with { RequestHeaders = ChaosHeader(chaosId), RetryPolicy = FastRetry },
            ct);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        completed.RetryCount.Should().BeGreaterThan(0);
        (await Sha256FileAsync(completed.FinalPath, ct)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the condition should become true");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
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

    private static TransferRetryPolicy FastRetry { get; } = new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(5),
        MaxDelay = TimeSpan.FromMilliseconds(50),
    };

    private static Dictionary<string, string> ChaosHeader(string chaosId) =>
        new() { [ChaosRegistry.HeaderName] = chaosId };

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
