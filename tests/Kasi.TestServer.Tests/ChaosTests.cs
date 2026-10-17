using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Kasi.TestServer.Chaos;

namespace Kasi.TestServer.Tests;

[Trait("Category", "Integration")]
public sealed class ChaosTests : IAsyncLifetime
{
    private const int OneMiB = 1024 * 1024;

    private TestServerHost _host = null!;
    private readonly List<HttpClient> _clients = [];

    public async ValueTask InitializeAsync() => _host = await TestServerHost.StartAsync(cancellationToken: TestClient.Ct);

    public async ValueTask DisposeAsync()
    {
        _clients.ForEach(c => c.Dispose());
        await _host.DisposeAsync();
    }

    private HttpClient Client(ChaosProfile? profile = null)
    {
        var client = TestClient.Create(_host, profile is null ? null : _host.Chaos.Register(profile));
        _clients.Add(client);
        return client;
    }

    [Fact]
    public async Task Reset_after_N_bytes_breaks_the_transfer_before_the_end()
    {
        var client = Client(new ChaosProfile { ResetAfterBytes = 100_000 });

        var (body, failure) = await TestClient.ReadBodyAsync(client, $"/files/x/{OneMiB}");

        failure.Should().NotBeNull();
        body.Length.Should().BeLessThanOrEqualTo(100_000);
    }

    [Fact]
    public async Task Reset_probability_of_one_breaks_after_the_first_64_KiB_and_zero_never_does()
    {
        var always = Client(new ChaosProfile { ResetProbabilityPer64KiB = 1 });
        var never = Client(new ChaosProfile { ResetProbabilityPer64KiB = 0 });

        var (brokenBody, failure) = await TestClient.ReadBodyAsync(always, $"/files/x/{OneMiB}");
        var (fullBody, noFailure) = await TestClient.ReadBodyAsync(never, $"/files/x/{OneMiB}");

        failure.Should().NotBeNull();
        brokenBody.Length.Should().BeLessThanOrEqualTo(64 * 1024);
        noFailure.Should().BeNull();
        Convert.ToHexStringLower(SHA256.HashData(fullBody)).Should().Be(GeneratedContent.Sha256Hex("x", OneMiB));
    }

    [Fact]
    public async Task Probabilistic_resets_replay_identically_for_the_same_seed()
    {
        async Task<bool[]> RunAsync(int seed)
        {
            var client = Client(new ChaosProfile { Seed = seed, ResetProbabilityPer64KiB = 0.05 });
            var outcomes = new List<bool>();
            for (var i = 0; i < 12; i++)
            {
                var (_, failure) = await TestClient.ReadBodyAsync(client, $"/files/x/{OneMiB}");
                outcomes.Add(failure is not null);
            }

            return [.. outcomes];
        }

        var first = await RunAsync(seed: 42);
        var replay = await RunAsync(seed: 42);

        replay.Should().Equal(first);
        first.Should().Contain(true, "a 5% chance per 64 KiB over 16 blocks and 12 requests resets at least once").And.Contain(false);
    }

    [Fact]
    public async Task Stall_holds_the_connection_then_continues_with_intact_data()
    {
        var client = Client(new ChaosProfile { StallSeconds = 1, StallAfterBytes = 1000 });
        var clock = Stopwatch.StartNew();

        var (body, failure) = await TestClient.ReadBodyAsync(client, "/files/s/10000");

        failure.Should().BeNull();
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(900));
        body.Should().Equal(GeneratedContent.Bytes("s", 10000));
    }

    [Fact]
    public async Task Throttle_limits_the_body_rate()
    {
        var client = Client(new ChaosProfile { ThrottleBytesPerSecond = 100_000 });
        var clock = Stopwatch.StartNew();

        var (body, failure) = await TestClient.ReadBodyAsync(client, "/files/t/100000");

        failure.Should().BeNull();
        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800));
        body.Should().Equal(GeneratedContent.Bytes("t", 100000));
    }

    [Fact]
    public async Task Throttle_can_target_a_single_connection_by_index()
    {
        var profile = new ChaosProfile { ThrottleBytesPerSecond = 100_000, ThrottleConnectionIndex = 1 };
        var fast = Client(profile); // first connection accepted: index 0
        var slow = Client(profile); // second: index 1
        await TestClient.ReadBodyAsync(fast, "/files/t/10");
        var clock = Stopwatch.StartNew();

        await TestClient.ReadBodyAsync(fast, "/files/t/100000");
        var fastTime = clock.Elapsed;
        clock.Restart();
        await TestClient.ReadBodyAsync(slow, "/files/t/100000");
        var slowTime = clock.Elapsed;

        fastTime.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
        slowTime.Should().BeGreaterThan(TimeSpan.FromMilliseconds(800));
    }

    [Theory]
    [InlineData(429, 7)]
    [InlineData(500, null)]
    [InlineData(502, null)]
    [InlineData(503, 3)]
    [InlineData(504, null)]
    public async Task Status_injection_answers_with_the_status_and_Retry_After(int status, int? retryAfter)
    {
        var client = Client(new ChaosProfile { StatusCode = status, RetryAfterSeconds = retryAfter });

        using var response = await client.GetAsync("/files/x/100", TestClient.Ct);

        ((int)response.StatusCode).Should().Be(status);
        response.Headers.RetryAfter?.Delta?.TotalSeconds.Should().Be(retryAfter);
    }

    [Fact]
    public async Task Status_injection_at_half_rate_is_a_mix_that_replays_for_the_same_seed()
    {
        async Task<int[]> RunAsync()
        {
            var client = Client(new ChaosProfile { Seed = 9, StatusCode = 503, StatusRate = 0.5 });
            var codes = new List<int>();
            for (var i = 0; i < 20; i++)
            {
                using var response = await client.GetAsync("/files/x/10", TestClient.Ct);
                codes.Add((int)response.StatusCode);
            }

            return [.. codes];
        }

        var first = await RunAsync();
        var replay = await RunAsync();

        replay.Should().Equal(first);
        first.Should().Contain(503).And.Contain(200);
    }

    [Fact]
    public async Task Connections_beyond_the_per_client_cap_are_rejected_with_503()
    {
        var profile = new ChaosProfile { MaxConnectionsPerClient = 2 };
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 3; i++)
        {
            using var response = await Client(profile).GetAsync("/files/x/10", TestClient.Ct); // each client keeps its own connection open
            codes.Add(response.StatusCode);
        }

        codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Content_swaps_to_new_bytes_and_a_new_ETag_after_N_requests()
    {
        var client = Client(new ChaosProfile { ChangeContentAfterRequests = 2 });
        var etags = new List<string?>();
        var bodies = new List<byte[]>();

        for (var i = 0; i < 4; i++)
        {
            using var response = await client.GetAsync("/files/c/1000", TestClient.Ct);
            etags.Add(response.Headers.ETag?.Tag);
            bodies.Add(await response.Content.ReadAsByteArrayAsync(TestClient.Ct));
        }

        etags[0].Should().Be(etags[1]);
        etags[2].Should().NotBe(etags[1]).And.Be(etags[3]);
        bodies[1].Should().Equal(GeneratedContent.Bytes("c", 1000));
        bodies[2].Should().NotEqual(bodies[1]);
    }

    [Fact]
    public async Task Truncation_delivers_fewer_bytes_than_declared()
    {
        var client = Client(new ChaosProfile { TruncateAfterBytes = 1000 });

        var (body, failure) = await TestClient.ReadBodyAsync(client, "/files/x/10000");

        (failure is not null || body.Length < 10000).Should().BeTrue();
        body.Length.Should().BeLessThanOrEqualTo(1000);
    }

    [Fact]
    public async Task Latency_delays_the_first_byte()
    {
        var client = Client(new ChaosProfile { LatencyMs = 400, LatencyJitterMs = 100 });
        var clock = Stopwatch.StartNew();

        using var response = await client.GetAsync("/files/x/10", TestClient.Ct);

        clock.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(350));
    }

    [Fact]
    public async Task Unknown_profile_id_is_a_bad_request()
    {
        using var client = TestClient.Create(_host, chaosId: "nope");

        using var response = await client.GetAsync("/files/x/10", TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Control_API_registers_a_profile_by_JSON_and_clears_it()
    {
        using var control = TestClient.Create(_host);

        using var created = await control.PostAsJsonAsync("/_chaos", new { statusCode = 503 }, TestClient.Ct);
        var id = (await created.Content.ReadFromJsonAsync<Dictionary<string, string>>(TestClient.Ct))!["id"];
        using var chaotic = TestClient.Create(_host, id);
        using var injected = await chaotic.GetAsync("/files/x/10", TestClient.Ct);
        using var cleared = await control.DeleteAsync("/_chaos", TestClient.Ct);
        using var afterClear = await chaotic.GetAsync("/files/x/10", TestClient.Ct);

        injected.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        cleared.StatusCode.Should().Be(HttpStatusCode.NoContent);
        afterClear.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
