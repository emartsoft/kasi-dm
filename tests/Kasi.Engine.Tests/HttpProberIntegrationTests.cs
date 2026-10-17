using System.Net;
using System.Security.Cryptography;
using Kasi.TestServer;
using Kasi.TestServer.Chaos;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-003")]
[Trait("Requirement", "KASI-ENG-007")]
[Trait("Requirement", "KASI-ENG-010")]
[Trait("Requirement", "KASI-RES-005")]
public sealed class HttpProberIntegrationTests : IAsyncLifetime
{
    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    [Trait("TC", "TC-ENG-003")]
    public async Task Probes_ranged_file_with_a_single_GET_and_streams_the_body_matching_SHA256()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/files/probe-seed/65536"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        counter.RequestCount.Should().Be(1);
        response.RangesSupported.Should().BeTrue();
        response.Resumable.Should().BeTrue();
        response.TotalBytes.Should().Be(65_536);
        response.ETag.Should().Be("\"probe-seed-65536\"");
        response.LastModified.Should().NotBeNullOrWhiteSpace();
        response.FileName.Should().Be("65536");
        response.ContentType.Should().Be("application/octet-stream");
        response.ContentEncoding.Should().BeNull();

        var bytes = await ReadAllBytesAsync(response.Body, ct);
        bytes.Should().HaveCount(65_536);
        Convert.ToHexStringLower(SHA256.HashData(bytes))
            .Should().Be(GeneratedContent.Sha256Hex("probe-seed", 65_536));
    }

    [Fact]
    [Trait("TC", "TC-ENG-012")]
    [Trait("TC", "TC-RES-010")]
    public async Task Probes_range_less_server_as_non_ranged_and_non_resumable_and_streams_full_body()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/norange/nr/5000"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        counter.RequestCount.Should().Be(1);
        response.RangesSupported.Should().BeFalse();
        response.Resumable.Should().BeFalse();
        response.TotalBytes.Should().Be(5000);

        var bytes = await ReadAllBytesAsync(response.Body, ct);
        Convert.ToHexStringLower(SHA256.HashData(bytes))
            .Should().Be(GeneratedContent.Sha256Hex("nr", 5000));
    }

    [Fact]
    [Trait("TC", "TC-RES-010")]
    public async Task Probes_validator_less_ranged_server_as_ranged_but_non_resumable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/novalidators/nv/5000"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        counter.RequestCount.Should().Be(1);
        response.RangesSupported.Should().BeTrue();
        response.ETag.Should().BeNull();
        response.LastModified.Should().BeNull();
        response.Resumable.Should().BeFalse();
        response.TotalBytes.Should().Be(5000);

        var bytes = await ReadAllBytesAsync(response.Body, ct);
        Convert.ToHexStringLower(SHA256.HashData(bytes))
            .Should().Be(GeneratedContent.Sha256Hex("nv", 5000));
    }

    [Fact]
    [Trait("TC", "TC-ENG-013")]
    public async Task Probes_chunked_endpoint_with_unknown_size_and_streams_full_body()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/chunked/ch/100000"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        counter.RequestCount.Should().Be(1);
        response.RangesSupported.Should().BeFalse();
        response.Resumable.Should().BeFalse();
        response.TotalBytes.Should().BeNull();

        var bytes = await ReadAllBytesAsync(response.Body, ct);
        bytes.Should().HaveCount(100_000);
        Convert.ToHexStringLower(SHA256.HashData(bytes))
            .Should().Be(GeneratedContent.Sha256Hex("ch", 100_000));
    }

    [Fact]
    public async Task Reprobes_without_Range_when_zero_length_file_answers_416()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/files/zero/0"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        counter.RequestCount.Should().Be(2);
        response.TotalBytes.Should().Be(0);
        response.RangesSupported.Should().BeFalse();
        response.Resumable.Should().BeFalse();

        var bytes = await ReadAllBytesAsync(response.Body, ct);
        bytes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [Trait("TC", "TC-ENG-017")]
    public async Task Follows_up_to_ten_redirects_and_records_the_final_URL_and_filename(int hops)
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url($"/redirect/{hops}/files/rd/1024"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        counter.RequestCount.Should().Be(hops + 1);
        response.FinalUrl.AbsolutePath.Should().EndWith("/files/rd/1024");
        response.FileName.Should().Be("1024");
        response.RangesSupported.Should().BeTrue();
        response.TotalBytes.Should().Be(1024);

        var bytes = await ReadAllBytesAsync(response.Body, ct);
        Convert.ToHexStringLower(SHA256.HashData(bytes))
            .Should().Be(GeneratedContent.Sha256Hex("rd", 1024));
    }

    [Fact]
    [Trait("TC", "TC-ENG-017")]
    public async Task Fails_with_TooManyRedirects_when_redirect_chain_exceeds_ten_hops()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/redirect/11/files/rd/1024"), cancellationToken: ct);

        var failure = outcome.Should().BeOfType<ProbeFailure>().Subject;
        failure.Kind.Should().Be(ProbeErrorKind.TooManyRedirects);
        counter.RequestCount.Should().Be(HttpProber.MaxRedirects + 1);
    }

    [Fact]
    public async Task Resolves_RFC_5987_filename_from_Content_Disposition_during_probe()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/disposition/rfc5987"), cancellationToken: ct);

        using var response = outcome.Should().BeOfType<ProbeResponse>().Subject;
        response.FileName.Should().Be("€ rates.txt");
    }

    [Fact]
    public async Task Classifies_401_with_Basic_challenge_and_403_signed_URL_as_AuthRequired()
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var basicOutcome = await prober.ProbeAsync(Url("/auth/basic/files/a/100"), cancellationToken: ct);
        var signedOutcome = await prober.ProbeAsync(Url("/signed/files/a/100"), cancellationToken: ct);

        var basicFailure = basicOutcome.Should().BeOfType<ProbeFailure>().Subject;
        basicFailure.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        basicFailure.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        basicFailure.AuthChallenges.Should().Equal("Basic");

        var signedFailure = signedOutcome.Should().BeOfType<ProbeFailure>().Subject;
        signedFailure.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        signedFailure.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/unknown", HttpStatusCode.NotFound)]
    [InlineData("/files/missing/-1", HttpStatusCode.BadRequest)]
    public async Task Classifies_404_and_400_as_PermanentError(string path, HttpStatusCode expectedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        using var counter = CreateCountingClient();
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url(path), cancellationToken: ct);

        var failure = outcome.Should().BeOfType<ProbeFailure>().Subject;
        failure.Kind.Should().Be(ProbeErrorKind.PermanentError);
        failure.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task Classifies_chaos_429_with_Retry_After_as_Throttled()
    {
        var ct = TestContext.Current.CancellationToken;
        var chaosId = _host.Chaos.Register(new ChaosProfile { StatusCode = 429, RetryAfterSeconds = 5 });
        using var counter = CreateCountingClient(chaosId);
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/files/c/1024"), cancellationToken: ct);

        var failure = outcome.Should().BeOfType<ProbeFailure>().Subject;
        failure.Kind.Should().Be(ProbeErrorKind.Throttled);
        failure.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        failure.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Classifies_chaos_503_without_Retry_After_as_TransientError()
    {
        var ct = TestContext.Current.CancellationToken;
        var chaosId = _host.Chaos.Register(new ChaosProfile { StatusCode = 503 });
        using var counter = CreateCountingClient(chaosId);
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/files/c/1024"), cancellationToken: ct);

        var failure = outcome.Should().BeOfType<ProbeFailure>().Subject;
        failure.Kind.Should().Be(ProbeErrorKind.TransientError);
        failure.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        failure.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task Classifies_chaos_connection_reset_before_headers_as_TransientError()
    {
        var ct = TestContext.Current.CancellationToken;
        var chaosId = _host.Chaos.Register(new ChaosProfile { ResetAfterBytes = 0 });
        using var counter = CreateCountingClient(chaosId);
        var prober = new HttpProber(counter.Client);

        var outcome = await prober.ProbeAsync(Url("/files/c/1024"), cancellationToken: ct);

        var failure = outcome.Should().BeOfType<ProbeFailure>().Subject;
        failure.Kind.Should().Be(ProbeErrorKind.TransientError);
        failure.StatusCode.Should().BeNull();
    }

    private Uri Url(string relativePath) => new(_host.BaseAddress, relativePath);

    private static CountingClient CreateCountingClient(string? chaosId = null) => new(chaosId);

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken);
        return ms.ToArray();
    }

    private sealed class CountingClient : IDisposable
    {
        private readonly CountingHandler _handler;

        public CountingClient(string? chaosId)
        {
            _handler = new CountingHandler(chaosId);
            Client = new HttpClient(_handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        public HttpClient Client { get; }

        public int RequestCount => _handler.RequestCount;

        public void Dispose() => Client.Dispose();
    }

#pragma warning disable CA2000 // base DelegatingHandler owns and disposes the inner SocketsHttpHandler
    private sealed class CountingHandler(string? chaosId) : DelegatingHandler(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
    })
#pragma warning restore CA2000
    {
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            if (chaosId is not null)
            {
                request.Headers.Remove(ChaosRegistry.HeaderName);
                request.Headers.TryAddWithoutValidation(ChaosRegistry.HeaderName, chaosId);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
