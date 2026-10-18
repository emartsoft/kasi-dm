using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Kasi.TestServer.Endpoints;

namespace Kasi.TestServer.Tests;

[Trait("Category", "Integration")]
public sealed class GateAndDispositionTests : IAsyncLifetime
{
    private TestServerHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestServerHost.StartAsync(cancellationToken: TestClient.Ct);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static string Basic(string user, string password) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task Redirect_chain_has_exactly_the_requested_number_of_hops_then_serves_the_file()
    {
        using var client = TestClient.Create(_host, autoRedirect: false);
        var url = "/redirect/3/files/rd/500";
        var hops = 0;

        HttpResponseMessage response;
        while ((response = await client.GetAsync(url, TestClient.Ct)).StatusCode == HttpStatusCode.Found)
        {
            hops++;
            url = response.Headers.Location!.OriginalString;
            response.Dispose();
        }

        using (response)
        {
            hops.Should().Be(3);
            (await response.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().Equal(GeneratedContent.Bytes("rd", 500));
        }
    }

    [Fact]
    public async Task Redirect_followed_automatically_ends_at_the_file()
    {
        using var client = TestClient.Create(_host);

        var body = await client.GetByteArrayAsync("/redirect/5/files/rd/500", TestClient.Ct);

        body.Should().Equal(GeneratedContent.Bytes("rd", 500));
    }

    [Fact]
    public async Task Basic_auth_rejects_missing_and_wrong_credentials_and_accepts_the_right_ones()
    {
        using var client = TestClient.Create(_host);

        using var anonymous = await client.GetAsync("/auth/basic/files/b/100", TestClient.Ct);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, "/auth/basic/files/b/100");
        wrong.Headers.Authorization = new AuthenticationHeaderValue("Basic", Basic("kasi", "nope"));
        using var wrongResponse = await client.SendAsync(wrong, TestClient.Ct);
        using var right = new HttpRequestMessage(HttpMethod.Get, "/auth/basic/files/b/100");
        right.Headers.Authorization = new AuthenticationHeaderValue("Basic", Basic(PathGates.BasicUser, PathGates.BasicPassword));
        using var rightResponse = await client.SendAsync(right, TestClient.Ct);

        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Basic");
        wrongResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await rightResponse.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().Equal(GeneratedContent.Bytes("b", 100));
    }

    [Fact]
    [Trait("TC", "TC-ENG-016")]
    public async Task Digest_auth_challenges_anonymous_and_wrong_credentials_then_serves_a_range()
    {
        var path = "/auth/digest/files/dg/1024";
        using var anonymousClient = TestClient.Create(_host);
        using var anonymous = await anonymousClient.GetAsync(path, TestClient.Ct);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymous.Headers.WwwAuthenticate.Should().ContainSingle().Which.Scheme.Should().Be("Digest");

        using var wrongHandler = new HttpClientHandler { Credentials = new NetworkCredential(PathGates.BasicUser, "wrong") };
        using var wrongClient = new HttpClient(wrongHandler, disposeHandler: false)
        {
            BaseAddress = _host.BaseAddress,
        };
        using var wrong = await wrongClient.GetAsync(path, TestClient.Ct);
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var rightHandler = new HttpClientHandler { Credentials = new NetworkCredential(PathGates.BasicUser, PathGates.BasicPassword) };
        using var rightClient = new HttpClient(rightHandler, disposeHandler: false)
        {
            BaseAddress = _host.BaseAddress,
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(0, 99);
        using var right = await rightClient.SendAsync(request, TestClient.Ct);
        right.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        (await right.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().Equal(GeneratedContent.Bytes("dg", 1024)[..100]);
    }

    [Fact]
    [Trait("TC", "TC-ENG-017")]
    public async Task Redirect_to_an_absolute_uri_returns_302_with_that_location()
    {
        var target = "http://localhost:5080/files/rd/100?token=abc";
        using var client = TestClient.Create(_host, autoRedirect: false);

        using var response = await client.GetAsync("/redirect-to?target=" + Uri.EscapeDataString(target), TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.Headers.Location!.AbsoluteUri.Should().Be(target);
    }

    [Fact]
    public async Task Cookie_auth_requires_the_session_cookie_issued_by_login()
    {
        using var client = TestClient.Create(_host, cookies: new CookieContainer());

        using var before = await client.GetAsync("/auth/cookie/files/k/100", TestClient.Ct);
        using var login = await client.GetAsync("/auth/cookie/login", TestClient.Ct);
        using var after = await client.GetAsync("/auth/cookie/files/k/100", TestClient.Ct);

        before.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        login.StatusCode.Should().Be(HttpStatusCode.NoContent);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        (await after.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().Equal(GeneratedContent.Bytes("k", 100));
    }

    [Fact]
    public async Task Signed_link_works_until_exp_and_is_403_after_it_or_without_it()
    {
        var fake = new MovableClock(DateTimeOffset.UtcNow);
        await _host.DisposeAsync();
        _host = await TestServerHost.StartAsync(fake, TestClient.Ct);
        using var client = TestClient.Create(_host);
        var exp = fake.GetUtcNow().AddSeconds(60).ToUnixTimeSeconds();

        using var valid = await client.GetAsync($"/signed/files/sg/100?exp={exp}", TestClient.Ct);
        fake.Advance(TimeSpan.FromSeconds(61));
        using var expired = await client.GetAsync($"/signed/files/sg/100?exp={exp}", TestClient.Ct);
        using var unsigned = await client.GetAsync("/signed/files/sg/100", TestClient.Ct);

        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        expired.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        unsigned.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Gates_nest()
    {
        using var client = TestClient.Create(_host);
        var exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();

        var body = await client.GetByteArrayAsync($"/redirect/2/signed/files/n/64?exp={exp}", TestClient.Ct);

        body.Should().Equal(GeneratedContent.Bytes("n", 64));
    }

    public static TheoryData<string> DispositionCaseNames => new(DispositionEndpoints.Cases.Keys);

    [Theory]
    [MemberData(nameof(DispositionCaseNames))]
    public async Task Disposition_case_is_served_verbatim_with_a_small_body(string name)
    {
        using var client = TestClient.Create(_host);

        using var response = await client.GetAsync($"/disposition/{name}", TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.GetValues("Content-Disposition").Should().ContainSingle().Which.Should().Be(DispositionEndpoints.Cases[name]);
        (await response.Content.ReadAsByteArrayAsync(TestClient.Ct)).Should().HaveCount(16);
    }

    [Fact]
    public async Task Unknown_disposition_case_is_404()
    {
        using var client = TestClient.Create(_host);

        using var response = await client.GetAsync("/disposition/nonsense", TestClient.Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>A <see cref="TimeProvider"/> whose clock only moves when the test says so.</summary>
    private sealed class MovableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
