using Kasi.Core;

namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-016")]
[Trait("Requirement", "KASI-ENG-009")]
public sealed class JobCookieMatcherTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/dl/file", true)]
    [InlineData("example.com", "https://sub.example.com/dl/file", false)]
    [InlineData(".example.com", "https://example.com/dl/file", true)]
    [InlineData(".example.com", "https://sub.example.com/dl/file", true)]
    [InlineData(".example.com", "https://badexample.com/dl/file", false)]
    [InlineData("127.0.0.1", "https://127.0.0.1:8443/dl/file", true)]
    [InlineData(".127.0.0.1", "https://127.0.0.1/dl/file", true)]
    [InlineData(".127.0.0.1", "https://foo.127.0.0.1/dl/file", false)]
    [InlineData("127.0.0.1", "https://127.0.0.2/dl/file", false)]
    public void Domain_scope_matches_host_or_label_bounded_subdomain(string domain, string url, bool expected)
    {
        var cookie = Cookie(domain);

        JobCookieMatcher.Matches(cookie, new Uri(url)).Should().Be(expected);
    }

    [Theory]
    [InlineData("/dl", "/dl", true)]
    [InlineData("/dl", "/dl/file.bin", true)]
    [InlineData("/dl", "/dl2/file.bin", false)]
    [InlineData("/dl/", "/dl/file.bin", true)]
    [InlineData("/dl/", "/dl", false)]
    [InlineData("/", "/anything", true)]
    public void Path_scope_uses_segment_boundaries(string path, string requestPath, bool expected)
    {
        JobCookieMatcher.Matches(Cookie("example.com") with { Path = path }, new Uri("https://example.com" + requestPath)).Should().Be(expected);
    }

    [Fact]
    public void Secure_cookie_requires_https_while_an_unsecured_cookie_allows_http()
    {
        var url = new Uri("http://example.com/dl/file.bin");

        JobCookieMatcher.Matches(Cookie("example.com") with { Secure = true }, url).Should().BeFalse();
        JobCookieMatcher.Matches(Cookie("example.com"), url).Should().BeTrue();
    }

    private static JobCookie Cookie(string domain) => new() { Name = "session", Value = "v", Domain = domain, Path = "/dl" };
}
