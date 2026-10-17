namespace Kasi.Engine.Tests;

[Trait("TC", "TC-ENG-004")]
[Trait("Requirement", "KASI-ENG-003")]
public class ContentDispositionParserTests
{
    [Theory]
    [InlineData("attachment; filename=\"rates.txt\"", "rates.txt")]
    [InlineData("attachment; filename=rates.txt", "rates.txt")]
    [InlineData("inline; filename=\"inline.pdf\"", "inline.pdf")]
    [InlineData("ATTACHMENT; FILENAME=\"Report.PDF\"", "Report.PDF")]
    [InlineData("attachment; filename=\"a;b.txt\"", "a;b.txt")]
    [InlineData("attachment; filename=\"a \\\"quoted\\\" name.txt\"", "a \"quoted\" name.txt")]
    [InlineData("attachment; filename=\"a \\\\backslash\\\\ name.txt\"", @"a \backslash\ name.txt")]
    [InlineData("attachment; filename=\"..\\..\\evil.txt\"", @"..\..\evil.txt")]
    [InlineData("attachment; filename=\"C:\\Windows\\System32\\drivers\\etc\\hosts\"", @"C:\Windows\System32\drivers\etc\hosts")]
    public void Parses_quoted_and_unquoted_plain_filename_parameters(string header, string expected)
    {
        var ok = ContentDispositionParser.TryGetFileName(header, out var fileName);

        ok.Should().BeTrue();
        fileName.Should().Be(expected);
    }

    [Theory]
    [InlineData("attachment; filename*=UTF-8''%E2%82%AC%20rates.txt", "€ rates.txt")]
    [InlineData("attachment; filename*=utf-8'en'%E2%82%AC%20rates.txt", "€ rates.txt")]
    [InlineData("attachment; filename*=UTF-8'en-US'Na%C3%AFve%20file.txt", "Naïve file.txt")]
    [InlineData("attachment; filename*=ISO-8859-1''%A3%20rates.txt", "£ rates.txt")]
    [InlineData("attachment; filename*=iso-8859-1'de'%E4%F6%FC.txt", "äöü.txt")]
    [InlineData("attachment; filename=\"rates.txt\"; filename*=UTF-8''%E2%82%AC%20rates.txt", "€ rates.txt")]
    [InlineData("attachment; filename*=UTF-8''%E2%82%AC%20rates.txt; filename=\"rates.txt\"", "€ rates.txt")]
    public void Parses_rfc5987_extended_filename_and_prefers_it_over_plain_filename_in_any_order(string header, string expected)
    {
        var ok = ContentDispositionParser.TryGetFileName(header, out var fileName);

        ok.Should().BeTrue();
        fileName.Should().Be(expected);
    }

    [Theory]
    [InlineData("attachment; filename*=UTF-8%20missing-ticks.txt; filename=\"fallback.txt\"", "fallback.txt")]
    [InlineData("attachment; filename*=UTF-8'only-one-tick.txt; filename=\"fallback.txt\"", "fallback.txt")]
    [InlineData("attachment; filename*=SHIFT_JIS''%82%A0.txt; filename=\"fallback.txt\"", "fallback.txt")]
    [InlineData("attachment; filename*=UTF-8''%ZZbad-hex.txt; filename=\"fallback.txt\"", "fallback.txt")]
    [InlineData("attachment; filename*=UTF-8''truncated%2; filename=\"fallback.txt\"", "fallback.txt")]
    [InlineData("attachment; filename*=UTF-8''%FF%FEinvalid-utf8.txt; filename=\"fallback.txt\"", "fallback.txt")]
    [InlineData("attachment; filename*=\"UTF-8''quoted-ext.txt\"; filename=\"fallback.txt\"", "fallback.txt")]
    public void Rejects_malformed_extended_filename_and_falls_back_to_plain_filename(string header, string expected)
    {
        var ok = ContentDispositionParser.TryGetFileName(header, out var fileName);

        ok.Should().BeTrue();
        fileName.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("attachment")]
    [InlineData("attachment;")]
    [InlineData("attachment; filename=\"\"")]
    [InlineData("attachment; filename=")]
    [InlineData("attachment; filename=\"unclosed.txt")]
    [InlineData("attachment; filename=unquoted with spaces.txt")]
    [InlineData("attachment; filename*=UTF-8''")]
    [InlineData("attachment; filename*=UTF-8''%ZZ")]
    [InlineData("attachment; filename*=UTF-8''%FF")]
    [InlineData("; filename=\"no-disposition-type.txt\"")]
    [InlineData("filename=\"missing-type.txt\"")]
    public void Returns_false_when_no_valid_filename_is_present(string? header)
    {
        var ok = ContentDispositionParser.TryGetFileName(header, out var fileName);

        ok.Should().BeFalse();
        fileName.Should().BeEmpty();
    }

    [Fact]
    public void Multi_header_overload_prefers_extended_filename_across_headers()
    {
        string[] headers =
        [
            "attachment; filename=\"fallback.txt\"",
            "attachment; filename*=UTF-8''%E2%82%AC%20rates.txt",
        ];

        var ok = ContentDispositionParser.TryGetFileName(headers, out var fileName);

        ok.Should().BeTrue();
        fileName.Should().Be("€ rates.txt");
        ContentDispositionParser.TryGetFileName((IEnumerable<string>?)null, out _).Should().BeFalse();
    }

    [Fact]
    public void Ignores_headers_exceeding_the_16_kib_bound()
    {
        var prefix = "attachment; filename=\"";
        var suffix = ".bin\"";
        var paddingAtLimit = new string('a', ContentDispositionParser.MaxHeaderLength - prefix.Length - suffix.Length);
        var atLimit = prefix + paddingAtLimit + suffix;
        var overLimit = prefix + paddingAtLimit + "a" + suffix;

        ContentDispositionParser.TryGetFileName(atLimit, out var parsed).Should().BeTrue();
        parsed.Should().Be(paddingAtLimit + ".bin");

        ContentDispositionParser.TryGetFileName(overLimit, out _).Should().BeFalse();
        ContentDispositionParser.TryGetFileName([atLimit, "attachment; filename=\"extra.txt\""], out _).Should().BeFalse();
    }
}
