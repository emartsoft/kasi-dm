using Kasi.Core.Logging;

namespace Kasi.Core.Tests.Logging;

[Trait("Requirement", "KASI-NFR-013")]
public class RedactTests
{
    [Theory]
    [InlineData("https://example.com/files/a.zip", "https://example.com/files/a.zip")]
    [InlineData("https://example.com/files/a.zip?token=abc&exp=1", "https://example.com/files/a.zip")]
    [InlineData("https://example.com/a.zip#section", "https://example.com/a.zip")]
    [InlineData("https://user:pa55@example.com/a.zip", "https://example.com/a.zip")]
    [InlineData("https://user:pa55@example.com:8443/a.zip?sig=x#y", "https://example.com:8443/a.zip")]
    [InlineData("http://127.0.0.1:5080/files/seed/10", "http://127.0.0.1:5080/files/seed/10")]
    [InlineData("http://[::1]:5080/x?k=v", "http://[::1]:5080/x")]
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("ftp://ftp.example.com/pub/a.iso?x=1", "ftp://ftp.example.com/pub/a.iso")]
    public void Url_keeps_scheme_host_port_and_path_and_drops_the_rest(string url, string expected)
    {
        Redact.Url(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef&dn=secret+name", "magnet:[redacted]")]
    [InlineData("data:text/plain;base64,c2VjcmV0", "data:[redacted]")]
    [InlineData("file:///C:/Users/bob/secret.txt", "file:[redacted]")]
    public void Url_without_a_host_keeps_only_the_scheme(string url, string expected)
    {
        Redact.Url(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/relative/path?token=abc")]
    [InlineData("example.com/a.zip?token=abc")]
    [InlineData("https://")]
    public void Url_that_is_not_an_absolute_http_style_url_becomes_the_placeholder(string url)
    {
        Redact.Url(url).Should().Be(Redact.Placeholder);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Url_of_nothing_is_empty(string? url, string expected)
    {
        Redact.Url(url).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\Users\bob\Downloads\Video\movie.mp4", "movie.mp4")]
    [InlineData("C:/Users/bob/Downloads/movie.mp4", "movie.mp4")]
    [InlineData(@"\\server\share\team\report.pdf", "report.pdf")]
    [InlineData("/home/bob/file.bin", "file.bin")]
    [InlineData("movie.mp4", "movie.mp4")]
    [InlineData(@"C:\Users\bob\Downloads\", "Downloads")]
    public void Path_keeps_only_the_last_segment(string path, string expected)
    {
        Redact.Path(path).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\Users\bob")]
    [InlineData(@"C:\Users\bob\")]
    [InlineData("c:/users/BOB")]
    [InlineData(@"C:\")]
    [InlineData("/")]
    [InlineData(@"\\")]
    public void Path_that_ends_at_a_profile_folder_or_a_root_becomes_the_placeholder(string path)
    {
        Redact.Path(path).Should().Be(Redact.Placeholder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Path_of_nothing_is_empty(string? path)
    {
        Redact.Path(path).Should().BeEmpty();
    }

    [Fact]
    public void Odd_input_never_throws()
    {
        var odd = new[] { "http://%", "http://[", "\0", new string('a', 10_000), "https://exa mple.com/a b?c d", "::", @"C:\Users\", "..\\..\\" };

        foreach (var value in odd)
        {
            var act = () => (Redact.Url(value), Redact.Path(value));
            act.Should().NotThrow(value.Length > 20 ? "a very long value" : value);
        }
    }
}
