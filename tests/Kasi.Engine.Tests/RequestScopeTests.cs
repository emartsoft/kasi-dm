using Kasi.Core;

namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-016")]
[Trait("Requirement", "KASI-NFR-013")]
public sealed class RequestScopeTests
{
    [Fact]
    public void Every_captured_secret_header_stays_on_the_initial_origin_whatever_its_name()
    {
        var scope = new RequestScope(
            new Uri("https://example.com/dl/file"),
            new Dictionary<string, string> { ["X-Caller"] = "caller-value" },
            new JobSecret { Headers = new Dictionary<string, string> { ["X-Api-Key"] = "api-key-value" } });
        using var same = new HttpRequestMessage(HttpMethod.Get, "https://example.com/dl/part");
        using var other = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.net/dl/part");

        scope.Apply(same);
        scope.Apply(other);

        same.Headers.GetValues("X-Api-Key").Should().ContainSingle().Which.Should().Be("api-key-value");
        other.Headers.Contains("X-Api-Key").Should().BeFalse("a captured header is a secret even when its name is not Authorization");
        other.Headers.GetValues("X-Caller").Should().ContainSingle("caller headers that are not sensitive still follow redirects");
    }

    [Fact]
    public void Secret_headers_win_on_a_clash_and_cookie_values_are_added_only_when_they_match()
    {
        var scope = new RequestScope(
            new Uri("https://example.com/dl/file"),
            new Dictionary<string, string> { ["authorization"] = "caller-auth", ["User-Agent"] = "Kasi-test", ["Cookie"] = "legacy=1" },
            new JobSecret
            {
                Headers = new Dictionary<string, string> { ["Authorization"] = "secret-auth" },
                Cookies = [new JobCookie { Name = "session", Value = "cookie-value", Domain = ".example.com", Path = "/dl", Secure = true }],
            });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/dl/file");

        scope.Apply(request);

        request.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("secret-auth");
        request.Headers.GetValues("Cookie").Should().ContainSingle().Which.Should().Be("legacy=1; session=cookie-value");
        request.Headers.UserAgent.ToString().Should().Be("Kasi-test");
    }

    [Theory]
    [InlineData("https://other.example.com/dl/file")]
    [InlineData("https://example.com:8443/dl/file")]
    [InlineData("http://example.com/dl/file")]
    public void Sensitive_headers_are_dropped_on_every_other_origin(string target)
    {
        var scope = new RequestScope(
            new Uri("https://example.com/dl/file"),
            new Dictionary<string, string>
            {
                ["Authorization"] = "caller-auth",
                ["Cookie"] = "caller-cookie",
                ["Proxy-Authorization"] = "caller-proxy",
                ["Accept-Language"] = "en",
            },
            new JobSecret { Headers = new Dictionary<string, string> { ["authorization"] = "secret-auth" } });
        using var request = new HttpRequestMessage(HttpMethod.Get, target);

        scope.Apply(request);

        request.Headers.Contains("Authorization").Should().BeFalse();
        request.Headers.Contains("Cookie").Should().BeFalse();
        request.Headers.Contains("Proxy-Authorization").Should().BeFalse();
        request.Headers.GetValues("Accept-Language").Should().ContainSingle().Which.Should().Be("en");
    }

    [Fact]
    public void A_cookie_is_checked_against_each_target_even_after_a_redirect()
    {
        var scope = new RequestScope(
            new Uri("http://source.example.com/start"),
            requestHeaders: null,
            new JobSecret { Cookies = [new JobCookie { Name = "target", Value = "v", Domain = "target.example.com" }] });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://target.example.com/file");

        scope.Apply(request);

        request.Headers.GetValues("Cookie").Should().ContainSingle().Which.Should().Be("target=v");
    }

    [Fact]
    public void An_unknown_initial_origin_suppresses_sensitive_raw_headers()
    {
        var scope = new RequestScope(
            initialUrl: null,
            new Dictionary<string, string>
            {
                ["Authorization"] = "source-auth",
                ["Cookie"] = "source-cookie",
                ["Accept-Language"] = "en",
            },
            secret: null);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.com/file");

        scope.Apply(request);

        request.Headers.Contains("Authorization").Should().BeFalse();
        request.Headers.Contains("Cookie").Should().BeFalse();
        request.Headers.GetValues("Accept-Language").Should().ContainSingle().Which.Should().Be("en");
    }

    [Fact]
    [Trait("TC", "TC-ENG-018")]
    public void A_saved_secret_remains_bound_to_its_old_origin_after_a_link_refresh()
    {
        var oldOrigin = new Uri("https://old.example.com/");
        var scope = new RequestScope(
            new Uri("https://new.example.com/file"),
            requestHeaders: null,
            new JobSecret
            {
                SensitiveOrigin = oldOrigin,
                Headers = new Dictionary<string, string> { ["Authorization"] = "old-secret" },
            });
        using var newRequest = new HttpRequestMessage(HttpMethod.Get, "https://new.example.com/file");
        using var oldRequest = new HttpRequestMessage(HttpMethod.Get, "https://old.example.com/file");

        scope.Apply(newRequest);
        scope.Apply(oldRequest);

        newRequest.Headers.Contains("Authorization").Should().BeFalse();
        oldRequest.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Be("old-secret");
    }
}
