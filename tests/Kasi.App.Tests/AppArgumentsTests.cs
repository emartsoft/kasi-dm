using Kasi.App.Startup;

namespace Kasi.App.Tests;

[Trait("TC", "TC-SYS-006")]
[Trait("Requirement", "KASI-SYS-005")]
public class AppArgumentsTests
{
    [Theory]
    [InlineData("http://example.com/a.zip")]
    [InlineData("https://example.com/a.zip?token=abc#frag")]
    [InlineData("ftp://example.com/pub/a.iso")]
    [InlineData("HTTPS://EXAMPLE.COM/A")]
    public void Http_https_and_ftp_urls_are_taken(string url)
    {
        var parsed = AppArguments.Parse([url]);

        parsed.Urls.Should().Equal(url);
        parsed.Rejected.Should().Be(0);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("data:text/plain;base64,AAAA")]
    [InlineData("blob:https://example.com/x")]
    [InlineData("\\\\server\\share\\a.zip")]
    [InlineData("C:\\a.zip")]
    [InlineData("example.com/a.zip")]
    [InlineData("--evil")]
    [InlineData("")]
    [InlineData("https://example.com/a\r\nb")]
    public void Anything_else_is_rejected_and_counted(string value)
    {
        var parsed = AppArguments.Parse([value]);

        parsed.Urls.Should().BeEmpty();
        parsed.Tray.Should().BeFalse();
        parsed.Rejected.Should().Be(1);
    }

    [Fact]
    public void A_url_of_8192_characters_is_taken_and_one_character_more_is_not()
    {
        var prefix = "https://example.com/";
        var fits = prefix + new string('a', 8192 - prefix.Length);

        AppArguments.Parse([fits]).Urls.Should().ContainSingle();
        AppArguments.Parse([fits + "a"]).Urls.Should().BeEmpty();
    }

    [Fact]
    public void Tray_is_recognised_in_any_case_and_urls_keep_their_order()
    {
        var parsed = AppArguments.Parse(["https://a.example/1", "--TRAY", "https://b.example/2"]);

        parsed.Tray.Should().BeTrue();
        parsed.Urls.Should().Equal("https://a.example/1", "https://b.example/2");
    }

    [Fact]
    public void No_arguments_means_no_urls_and_no_tray()
    {
        var parsed = AppArguments.Parse([]);

        parsed.Tray.Should().BeFalse();
        parsed.Urls.Should().BeEmpty();
        parsed.Rejected.Should().Be(0);
    }

    [Fact]
    public void What_is_handed_over_parses_back_to_the_same_request_without_the_rejected_part()
    {
        var parsed = AppArguments.Parse(["--tray", "https://a.example/1", "junk"]);

        var again = AppArguments.Parse(parsed.ToHandOff());

        again.Tray.Should().BeTrue();
        again.Urls.Should().Equal(parsed.Urls);
        again.Rejected.Should().Be(0);
    }
}
