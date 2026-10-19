namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-011")]
[Trait("TC", "TC-ENG-019")]
public sealed class CustomWebProxyTests
{
    private static CustomWebProxy Proxy(params string[] bypass) =>
        new(new ProxySettings { Mode = ProxyMode.Custom, Uri = new Uri("http://proxy.test:3128"), BypassHosts = bypass }, credentials: null);

    [Theory]
    [InlineData("http://files.example.com/a", true)]
    [InlineData("http://a.b.example.com/a", true)]
    [InlineData("http://example.com/a", false)]
    [InlineData("http://evilexample.com/a", false)]
    [InlineData("http://example.com.evil.test/a", false)]
    public void A_leading_dot_entry_bypasses_subdomains_only(string url, bool bypassed)
    {
        Proxy(".example.com").IsBypassed(new Uri(url)).Should().Be(bypassed);
    }

    [Theory]
    [InlineData("http://example.com/a", true)]
    [InlineData("http://EXAMPLE.com/a", true)]
    [InlineData("http://files.example.com/a", false)]
    public void A_plain_entry_bypasses_that_exact_host_only(string url, bool bypassed)
    {
        Proxy("example.com").IsBypassed(new Uri(url)).Should().Be(bypassed);
    }

    [Fact]
    public void Everything_else_goes_through_the_proxy_and_a_bypassed_host_goes_direct()
    {
        var proxy = Proxy(".example.com");

        proxy.GetProxy(new Uri("http://other.test/a")).Should().Be(new Uri("http://proxy.test:3128"));
        proxy.GetProxy(new Uri("http://files.example.com/a")).Should().Be(new Uri("http://files.example.com/a"));
    }
}
