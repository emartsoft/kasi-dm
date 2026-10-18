using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Time.Testing;

#pragma warning disable CA2000 // StubHandler responses and cloned request inspection copies are disposed by the prober or test scope

namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-003")]
[Trait("TC", "TC-ENG-012")]
[Trait("TC", "TC-ENG-013")]
[Trait("TC", "TC-ENG-017")]
[Trait("TC", "TC-ENG-023")]
[Trait("TC", "TC-RES-010")]
[Trait("Requirement", "KASI-ENG-003")]
[Trait("Requirement", "KASI-ENG-007")]
[Trait("Requirement", "KASI-ENG-010")]
[Trait("Requirement", "KASI-RES-005")]
[Trait("Requirement", "KASI-NFR-011")]
public class HttpProberUnitTests
{
    [Fact]
    public async Task Sends_get_with_range_zero_and_accept_encoding_identity_or_bounded_first_range()
    {
        var requests = new List<HttpRequestMessage>();
        using var handler = new StubHandler((req, _) =>
        {
            requests.Add(CloneRequestHeaders(req));
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[64]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 63, 1024);
            response.Content.Headers.ContentLength = 64;
            response.Headers.ETag = new EntityTagHeaderValue("\"strong-1\"");
            return Task.FromResult(response);
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);

        var openEnded = await prober.ProbeAsync(
            new Uri("https://example.com/file.bin"),
            cancellationToken: TestContext.Current.CancellationToken);
        var bounded = await prober.ProbeAsync(
            new Uri("https://example.com/file.bin"),
            firstRangeLength: 64,
            cancellationToken: TestContext.Current.CancellationToken);

        using (var first = openEnded.Should().BeOfType<ProbeResponse>().Subject)
        using (var second = bounded.Should().BeOfType<ProbeResponse>().Subject)
        {
            requests.Should().HaveCount(2);
            requests[0].Method.Should().Be(HttpMethod.Get);
            requests[0].Headers.Range!.ToString().Should().Be("bytes=0-");
            requests[0].Headers.AcceptEncoding.Select(e => e.Value).Should().Contain("identity");

            requests[1].Headers.Range!.ToString().Should().Be("bytes=0-63");
            first.TotalBytes.Should().Be(1024);
            first.BodyLength.Should().Be(64);
            first.RangesSupported.Should().BeTrue();
            first.Resumable.Should().BeTrue();
            second.BodyLength.Should().Be(64);
        }
    }

    [Fact]
    public async Task Weak_etag_without_last_modified_is_not_resumable_while_weak_etag_with_last_modified_is_resumable()
    {
        var call = 0;
        using var handler = new StubHandler((_, _) =>
        {
            call++;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
            response.Headers.ETag = new EntityTagHeaderValue("\"weak-tag\"", isWeak: true);
            if (call == 2)
            {
                response.Content.Headers.LastModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            }

            return Task.FromResult(response);
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);

        using var weakOnly = (await prober.ProbeAsync(
            new Uri("https://example.com/a.bin"),
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeOfType<ProbeResponse>().Subject;
        using var weakWithLastModified = (await prober.ProbeAsync(
            new Uri("https://example.com/a.bin"),
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeOfType<ProbeResponse>().Subject;

        weakOnly.ETag.Should().BeNull();
        weakOnly.LastModified.Should().BeNull();
        weakOnly.RangesSupported.Should().BeTrue();
        weakOnly.Resumable.Should().BeFalse();

        weakWithLastModified.ETag.Should().BeNull();
        weakWithLastModified.LastModified.Should().Be("Thu, 01 Jan 2026 00:00:00 GMT");
        weakWithLastModified.Resumable.Should().BeTrue();
    }

    [Fact]
    public async Task Partial_content_with_missing_or_nonzero_content_range_fails_with_protocol_error_and_unknown_total_is_accepted()
    {
        var call = 0;
        using var handler = new StubHandler((_, _) =>
        {
            call++;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[10]),
            };
            if (call == 2)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(10, 19, 100);
            }
            else if (call == 3)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 9);
                response.Headers.ETag = new EntityTagHeaderValue("\"star\"");
            }

            return Task.FromResult(response);
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);
        var ct = TestContext.Current.CancellationToken;

        var missingRange = (await prober.ProbeAsync(new Uri("https://example.com/a.bin?secret=1"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;
        var nonZeroStart = (await prober.ProbeAsync(new Uri("https://example.com/a.bin?secret=1"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;
        using var unknownTotal = (await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: ct))
            .Should().BeOfType<ProbeResponse>().Subject;

        missingRange.Kind.Should().Be(ProbeErrorKind.ProtocolError);
        missingRange.Message.Should().NotContain("secret=1");
        nonZeroStart.Kind.Should().Be(ProbeErrorKind.ProtocolError);

        unknownTotal.RangesSupported.Should().BeTrue();
        unknownTotal.TotalBytes.Should().BeNull();
        unknownTotal.BodyLength.Should().Be(10);
    }

    [Fact]
    public async Task Non_identity_content_encoding_forces_single_connection_and_non_resumable()
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
            response.Content.Headers.ContentEncoding.Add("gzip");
            response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
            return Task.FromResult(response);
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);

        using var outcome = (await prober.ProbeAsync(
            new Uri("https://example.com/data.json"),
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeOfType<ProbeResponse>().Subject;

        outcome.ContentEncoding.Should().Be("gzip");
        outcome.RangesSupported.Should().BeFalse();
        outcome.Resumable.Should().BeFalse();
    }

    [Fact]
    public async Task Reprobes_without_range_on_first_416_and_fails_with_protocol_error_on_second_416()
    {
        var seenRanges = new List<string?>();
        using var firstHandler = new StubHandler((req, _) =>
        {
            seenRanges.Add(req.Headers.Range?.ToString());
            if (seenRanges.Count == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            }

            var ok = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([]),
            };
            ok.Content.Headers.ContentLength = 0;
            return Task.FromResult(ok);
        });
        using var invoker1 = new HttpMessageInvoker(firstHandler);
        var prober1 = new HttpProber(invoker1);

        using var reprobed = (await prober1.ProbeAsync(
            new Uri("https://example.com/empty.bin"),
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeOfType<ProbeResponse>().Subject;

        seenRanges.Should().Equal("bytes=0-", null);
        reprobed.TotalBytes.Should().Be(0);
        reprobed.RangesSupported.Should().BeFalse();
        reprobed.Resumable.Should().BeFalse();

        using var always416 = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable)));
        using var invoker2 = new HttpMessageInvoker(always416);
        var prober2 = new HttpProber(invoker2);

        var secondFailure = (await prober2.ProbeAsync(
            new Uri("https://example.com/broken.bin?token=abc"),
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeOfType<ProbeFailure>().Subject;

        secondFailure.Kind.Should().Be(ProbeErrorKind.ProtocolError);
        secondFailure.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        secondFailure.Message.Should().NotContain("token=abc");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ftp://example.com/file.bin")]
    [InlineData("file:///C:/Windows/win.ini")]
    public async Task Rejects_redirects_with_missing_or_non_http_location(string? location)
    {
        using var handler = new StubHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            if (location is not null)
            {
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            }

            return Task.FromResult(response);
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);

        var failure = (await prober.ProbeAsync(
            new Uri("https://example.com/start?sig=secret"),
            cancellationToken: TestContext.Current.CancellationToken)).Should().BeOfType<ProbeFailure>().Subject;

        failure.Kind.Should().Be(ProbeErrorKind.InvalidRedirect);
        failure.StatusCode.Should().Be(HttpStatusCode.Found);
        failure.Message.Should().NotContain("sig=secret");
    }

    [Fact]
    public async Task Populates_throttled_retry_after_from_delta_or_date_and_extracts_auth_challenges()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var call = 0;
        using var handler = new StubHandler((_, _) =>
        {
            call++;
            if (call == 1)
            {
                var r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r429.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
                return Task.FromResult(r429);
            }

            if (call == 2)
            {
                var r503 = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                r503.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(12));
                return Task.FromResult(r503);
            }

            var r401 = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            r401.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Basic", "realm=\"kasi\""));
            r401.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Digest", "realm=\"kasi\""));
            return Task.FromResult(r401);
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker, clock);
        var ct = TestContext.Current.CancellationToken;

        var f429 = (await prober.ProbeAsync(new Uri("https://example.com/a.bin?k=v"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;
        var f503 = (await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;
        var f401 = (await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;

        f429.Kind.Should().Be(ProbeErrorKind.Throttled);
        f429.RetryAfter.Should().Be(TimeSpan.FromSeconds(5));
        f429.Message.Should().NotContain("k=v");

        f503.Kind.Should().Be(ProbeErrorKind.Throttled);
        f503.RetryAfter.Should().Be(TimeSpan.FromSeconds(12));

        f401.Kind.Should().Be(ProbeErrorKind.AuthRequired);
        f401.AuthChallenges.Should().Equal("Basic", "Digest");
    }

    [Fact]
    public async Task Failure_messages_never_repeat_the_text_of_the_underlying_exception()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException(
            HttpRequestError.ConnectionError,
            "connect to https://example.com/a.bin?token=abc failed for C:/Users/someone/secret.txt",
            new SocketException((int)SocketError.ConnectionRefused)));
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);

        var failure = (await prober.ProbeAsync(new Uri("https://example.com/a.bin?token=abc"), cancellationToken: TestContext.Current.CancellationToken))
            .Should().BeOfType<ProbeFailure>().Subject;

        failure.Kind.Should().Be(ProbeErrorKind.TransientError);
        failure.Message.Should().NotContain("token=abc").And.NotContain("someone").And.NotContain("secret.txt");
        failure.Message.Should().Contain("ConnectionError").And.Contain("ConnectionRefused", "the error kinds are safe and still diagnostic");
    }

    [Fact]
    public async Task Classifies_certificate_errors_network_errors_timeouts_and_caller_cancellation()
    {
        var call = 0;
        using var handler = new StubHandler((_, _) =>
        {
            call++;
            return call switch
            {
                1 => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "SSL error"),
                2 => throw new HttpRequestException("TLS failed", new AuthenticationException("cert expired")),
                3 => throw new HttpRequestException(HttpRequestError.NameResolutionError, "DNS failed"),
                _ => throw new TaskCanceledException("HttpClient.Timeout elapsed"),
            };
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);
        var ct = TestContext.Current.CancellationToken;

        var sslError = (await prober.ProbeAsync(new Uri("https://example.com/a.bin?tok=1"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;
        var authEx = (await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;
        var dnsError = (await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;
        var timeout = (await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: ct))
            .Should().BeOfType<ProbeFailure>().Subject;

        sslError.Kind.Should().Be(ProbeErrorKind.CertificateError);
        sslError.Message.Should().NotContain("tok=1");
        authEx.Kind.Should().Be(ProbeErrorKind.CertificateError);
        dnsError.Kind.Should().Be(ProbeErrorKind.TransientError);
        timeout.Kind.Should().Be(ProbeErrorKind.TransientError);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var cancelledAct = async () => await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: cts.Token);
        await cancelledAct.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Disposes_response_when_caller_cancels_after_headers_arrive()
    {
        using var cts = new CancellationTokenSource();
        var streamDisposed = false;

        using var handler = new StubHandler(async (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CallbackDisposeStream(() => streamDisposed = true)),
            };
            await cts.CancelAsync();
            return response;
        });
        using var invoker = new HttpMessageInvoker(handler);
        var prober = new HttpProber(invoker);

        var act = async () => await prober.ProbeAsync(new Uri("https://example.com/a.bin"), cancellationToken: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        streamDisposed.Should().BeTrue();
    }

    private static HttpRequestMessage CloneRequestHeaders(HttpRequestMessage source)
    {
        var copy = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return copy;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class CallbackDisposeStream(Action onDispose) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose();
            }

            base.Dispose(disposing);
        }
    }
}
