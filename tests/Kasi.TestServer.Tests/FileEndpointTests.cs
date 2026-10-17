using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Kasi.TestServer.Tests;

[Trait("Category", "Integration")]
public sealed class FileEndpointTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestClient.Ct);
        _client = TestClient.Create(_host);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _host.DisposeAsync();
    }

    private static HttpRequestMessage Get(string url, RangeHeaderValue? range = null, RangeConditionHeaderValue? ifRange = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = range;
        request.Headers.IfRange = ifRange;
        return request;
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4096L)]
    [InlineData(4097L)]
    [InlineData(1_048_576L)]
    public async Task Full_download_matches_the_expected_SHA256_and_advertises_range_support(long size)
    {
        using var response = await _client.GetAsync($"/files/boundary/{size}", TestClient.Ct);
        var body = await response.Content.ReadAsByteArrayAsync(TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Convert.ToHexStringLower(SHA256.HashData(body)).Should().Be(GeneratedContent.Sha256Hex("boundary", size));
        response.Content.Headers.ContentLength.Should().Be(size);
        response.Headers.AcceptRanges.Should().Contain("bytes");
        response.Headers.ETag.Should().NotBeNull();
        response.Content.Headers.LastModified.Should().NotBeNull();
    }

    [Theory]
    [InlineData(100L, 199L)]
    [InlineData(0L, 0L)]
    [InlineData(900L, 999L)]
    [InlineData(990L, 5000L)]
    public async Task Byte_range_returns_206_with_exactly_that_slice(long from, long to)
    {
        using var request = Get("/files/r/1000", new RangeHeaderValue(from, to));
        using var response = await _client.SendAsync(request, TestClient.Ct);
        var body = await response.Content.ReadAsByteArrayAsync(TestClient.Ct);

        var last = Math.Min(to, 999);
        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().Should().Be($"bytes {from}-{last}/1000");
        body.Should().Equal(GeneratedContent.Bytes("r", 1000)[(int)from..(int)(last + 1)]);
    }

    [Fact]
    public async Task Open_ended_and_suffix_ranges_are_supported()
    {
        var all = GeneratedContent.Bytes("r", 1000);

        using var open = Get("/files/r/1000", new RangeHeaderValue(990, null));
        using var suffix = Get("/files/r/1000", new RangeHeaderValue(null, 10));
        using var openResponse = await _client.SendAsync(open, TestClient.Ct);
        using var suffixResponse = await _client.SendAsync(suffix, TestClient.Ct);

        (await openResponse.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().Equal(all[990..]);
        (await suffixResponse.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().Equal(all[990..]);
    }

    [Fact]
    public async Task Range_past_the_end_is_416_with_the_total_size()
    {
        using var request = Get("/files/r/1000", new RangeHeaderValue(5000, 6000));
        using var response = await _client.SendAsync(request, TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        response.Content.Headers.ContentRange!.ToString().Should().Be("bytes */1000");
    }

    [Fact]
    public async Task Range_deep_inside_a_20_GiB_file_returns_the_right_bytes()
    {
        const long size = 20L * 1024 * 1024 * 1024;
        const long from = size - 600;
        using var request = Get($"/files/huge/{size}", new RangeHeaderValue(from, from + 499));
        using var response = await _client.SendAsync(request, TestClient.Ct);

        var body = await response.Content.ReadAsByteArrayAsync(TestClient.Ct);

        response.Content.Headers.ContentRange!.Length.Should().Be(size);
        var expected = new byte[500];
        GeneratedContent.Fill("huge", from, expected);
        body.Should().Equal(expected);
    }

    [Fact]
    public async Task If_Range_with_the_current_ETag_resumes_and_with_a_stale_one_restarts()
    {
        using var first = await _client.GetAsync("/files/ir/1000", TestClient.Ct);
        var etag = first.Headers.ETag!;

        using var matching = Get("/files/ir/1000", new RangeHeaderValue(500, null), new RangeConditionHeaderValue(etag));
        using var stale = Get("/files/ir/1000", new RangeHeaderValue(500, null), new RangeConditionHeaderValue(new EntityTagHeaderValue("\"old\"")));
        using var matchingResponse = await _client.SendAsync(matching, TestClient.Ct);
        using var staleResponse = await _client.SendAsync(stale, TestClient.Ct);

        matchingResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        staleResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await staleResponse.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().HaveCount(1000);
    }

    [Fact]
    public async Task Head_returns_the_headers_without_a_body()
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, "/files/h/12345");
        using var response = await _client.SendAsync(request, TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(12345);
        response.Headers.ETag.Should().NotBeNull();
    }

    [Theory]
    [InlineData("/files/a/-1")]
    [InlineData("/novalidators/a/-1")]
    [InlineData("/norange/a/-1")]
    [InlineData("/chunked/a/-1")]
    public async Task Negative_size_is_a_bad_request(string url)
    {
        using var response = await _client.GetAsync(url, TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Novalidators_supports_Range_without_ETag_or_Last_Modified()
    {
        using var request = Get("/novalidators/nv/5000", new RangeHeaderValue(0, null));
        using var response = await _client.SendAsync(request, TestClient.Ct);
        var body = await response.Content.ReadAsByteArrayAsync(TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        response.Content.Headers.ContentRange!.ToString().Should().Be("bytes 0-4999/5000");
        response.Headers.ETag.Should().BeNull();
        response.Content.Headers.LastModified.Should().BeNull();
        body.Should().Equal(GeneratedContent.Bytes("nv", 5000));
    }

    [Fact]
    public async Task Norange_ignores_Range_and_always_returns_the_whole_file_with_200()
    {
        using var request = Get("/norange/n/5000", new RangeHeaderValue(100, 199));
        using var response = await _client.SendAsync(request, TestClient.Ct);
        var body = await response.Content.ReadAsByteArrayAsync(TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.AcceptRanges.Should().Contain("none");
        response.Content.Headers.ContentRange.Should().BeNull();
        body.Should().Equal(GeneratedContent.Bytes("n", 5000));
    }

    [Fact]
    public async Task Chunked_has_no_Content_Length_and_still_matches_the_expected_hash()
    {
        using var response = await _client.GetAsync("/chunked/c/300000", HttpCompletionOption.ResponseHeadersRead, TestClient.Ct);

        response.Content.Headers.Contains("Content-Length").Should().BeFalse();
        response.Headers.TransferEncodingChunked.Should().BeTrue();
        var body = await response.Content.ReadAsByteArrayAsync(TestClient.Ct);
        Convert.ToHexStringLower(SHA256.HashData(body)).Should().Be(GeneratedContent.Sha256Hex("c", 300000));
    }

    [Fact]
    public async Task Mutable_file_changes_bytes_and_ETag_when_the_test_changes_it()
    {
        using var before = await _client.GetAsync("/mutable/m1", TestClient.Ct);
        var beforeBody = await before.Content.ReadAsByteArrayAsync(TestClient.Ct);

        _host.Mutable.Change("m1");
        using var after = await _client.GetAsync("/mutable/m1", TestClient.Ct);
        var afterBody = await after.Content.ReadAsByteArrayAsync(TestClient.Ct);

        after.Headers.ETag.Should().NotBe(before.Headers.ETag);
        afterBody.Should().HaveCount(beforeBody.Length).And.NotEqual(beforeBody);
    }

    [Fact]
    public async Task Mutable_file_can_be_changed_over_HTTP_including_its_size()
    {
        using var change = await _client.PostAsync("/_mutable/m2/change?size=2048", content: null, TestClient.Ct);
        using var response = await _client.GetAsync("/mutable/m2", TestClient.Ct);

        change.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(2048);
    }
}
