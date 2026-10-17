using System.Text;

namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-004")]
[Trait("TC", "TC-ENG-005")]
[Trait("Requirement", "KASI-ENG-003")]
public class DownloadFileNameTests
{
    [Fact]
    public void Prefers_content_disposition_over_url_path_and_sanitises_the_result()
    {
        var url = new Uri("https://example.com/downloads/blob-12345?sig=abc");

        var resolved = DownloadFileName.Resolve(url, "attachment; filename=\"../../Setup.exe\"");

        resolved.Should().Be("Setup.exe");
        FileNameSanitiserTests.AssertStaysInTargetFolder(resolved);
    }

    [Theory]
    [InlineData("https://example.com/files/archive.tar.gz", null, "archive.tar.gz")]
    [InlineData("https://example.com/files/archive.tar.gz?token=secret#part", "", "archive.tar.gz")]
    [InlineData("https://example.com/files/%E2%82%AC%20rates.txt?v=1", "attachment", "€ rates.txt")]
    [InlineData("https://example.com/files/..%2F..%2Fevil.txt", "attachment; filename=\"\"", "evil.txt")]
    [InlineData("https://example.com/files/CON.txt", "attachment; filename=\"...\"", "_CON.txt")]
    [InlineData("ftp://ftp.example.com/pub/linux.iso?mirror=ke", null, "linux.iso")]
    [InlineData("/disposition/empty", "attachment; filename=\"\"", "empty")]
    [InlineData("/disposition/no-filename", "attachment", "no-filename")]
    public void Falls_back_to_url_path_when_content_disposition_has_no_usable_filename(
        string url,
        string? contentDisposition,
        string expected)
    {
        var fromString = DownloadFileName.Resolve(url, contentDisposition);
        var fromUri = DownloadFileName.Resolve(new Uri(url, UriKind.RelativeOrAbsolute), contentDisposition);

        fromString.Should().Be(expected);
        fromUri.Should().Be(expected);
        FileNameSanitiserTests.AssertStaysInTargetFolder(fromString);
    }

    [Fact]
    public void Falls_back_from_unusable_extended_filename_to_plain_filename_in_same_header()
    {
        var header = "attachment; filename*=UTF-8''..%2F..%2F; filename=\"fallback.txt\"";

        var resolved = DownloadFileName.Resolve("https://example.com/url-name.bin", header);

        resolved.Should().Be("fallback.txt");
    }

    [Fact]
    public void Repairs_utf8_bytes_decoded_as_latin1_in_plain_filename_but_never_in_extended_filename()
    {
        var utf8AsLatin1 = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("€ rates — 2026.txt"));
        var plainHeader = $"attachment; filename=\"{utf8AsLatin1}\"";

        DownloadFileName.Resolve("https://example.com/fallback.txt", plainHeader)
            .Should().Be("€ rates — 2026.txt");

        // Genuine single-byte Latin-1 (0xE9 'é' alone is not valid UTF-8) stays untouched.
        DownloadFileName.Resolve("https://example.com/fallback.txt", "attachment; filename=\"caf\u00E9.txt\"")
            .Should().Be("café.txt");

        // filename* explicitly declares ISO-8859-1: %C3%A9 must stay "Ã©.txt" and never be re-interpreted as UTF-8 "é.txt".
        DownloadFileName.Resolve("https://example.com/fallback.txt", "attachment; filename*=ISO-8859-1''%C3%A9.txt")
            .Should().Be("Ã©.txt");

        DownloadFileName.Resolve(new Uri("https://example.com/fallback.txt"), [plainHeader])
            .Should().Be("€ rates — 2026.txt");
    }

    [Theory]
    [InlineData("https://example.com/files/100%coverage.pdf", "100%coverage.pdf")]
    [InlineData("https://example.com/files/bad%ZZname.txt", "bad%ZZname.txt")]
    [InlineData("https://example.com/files/trailing%", "trailing%")]
    [InlineData("https://example.com/files/half%2", "half%2")]
    public void Tolerates_malformed_percent_sequences_in_url_paths_without_throwing(string url, string expected)
    {
        var resolved = DownloadFileName.Resolve(url);

        resolved.Should().Be(expected);
        FileNameSanitiserTests.AssertStaysInTargetFolder(resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://example.com")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/folder/")]
    [InlineData("https://example.com/folder/?id=123")]
    [InlineData("https://example.com/..")]
    [InlineData("magnet:?xt=urn:btih:0123456789abcdef")]
    [InlineData("data:text/plain;base64,SGVsbG8=")]
    [InlineData("http://")]
    public void Falls_back_to_default_filename_when_neither_header_nor_url_provides_a_name(string? url)
    {
        DownloadFileName.Resolve(url).Should().Be(FileNameSanitiser.DefaultFileName);
    }

    [Fact]
    public void Bounds_url_path_at_4_kib()
    {
        var overLimitPath = "https://example.com/" + new string('a', DownloadFileName.MaxUrlPathLength) + "/file.zip";

        DownloadFileName.Resolve(overLimitPath).Should().Be(FileNameSanitiser.DefaultFileName);
        DownloadFileName.Resolve(new Uri(overLimitPath)).Should().Be(FileNameSanitiser.DefaultFileName);
        DownloadFileName.Resolve((Uri?)null).Should().Be(FileNameSanitiser.DefaultFileName);
    }
}
