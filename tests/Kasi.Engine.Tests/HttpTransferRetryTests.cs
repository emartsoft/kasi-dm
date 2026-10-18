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

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private static HttpTransferOptions Options(int initialConnections) => new()
    {
        SchedulerOptions = new SegmentSchedulerOptions
        {
            MaxConnections = initialConnections,
            InitialConnections = initialConnections,
        },
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
