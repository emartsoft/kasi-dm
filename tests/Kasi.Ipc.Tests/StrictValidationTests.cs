using System.Text;
using Kasi.Ipc.Messages;

namespace Kasi.Ipc.Tests;

[Trait("TC", "TC-BRW-006")]
[Trait("Requirement", "KASI-NFR-010")]
public class StrictValidationTests
{
    private static readonly IpcCookie _cookie = new() { Name = "session", Value = "s3cr3t", Domain = ".example.com", Path = "/", Secure = true, HttpOnly = true };

    private static AddDownload Download(string? url = null, IReadOnlyList<IpcCookie>? cookies = null, string? filename = null) => new()
    {
        Id = "a1",
        Url = url ?? "https://example.com/setup.exe",
        FinalUrl = "https://example.com/setup.exe",
        Filename = filename ?? "setup.exe",
        Mime = "application/octet-stream",
        UserAgent = "test",
        Cookies = cookies ?? [_cookie],
    };

    private static void Rejected(IpcMessage message) =>
        ((Action)(() => IpcSerializer.Serialize(message))).Should().Throw<IpcProtocolException>().Which.Code.Should().Be(IpcErrorCode.InvalidMessage);

    private static IpcErrorCode ParseCode(string json) =>
        ((Action)(() => IpcSerializer.Deserialize(Encoding.UTF8.GetBytes(json)))).Should().Throw<IpcProtocolException>().Which.Code;

    [Fact]
    public void Every_type_of_message_survives_a_round_trip_through_the_serializer()
    {
        IpcMessage[] messages =
        [
            new Hello { Id = "1", Client = BrowserClient.Firefox, ExtensionVersion = "1.2.3", ProtocolMin = 1, ProtocolMax = 1 },
            new Rules { Id = "2", Enabled = true, Extensions = ["zip"], MimeTypes = [], ExcludedSites = ["a.example"], BypassKey = BypassKey.None, AckTimeoutMs = 2000 },
            Download(),
            new AddBatch { Id = "4", Referrer = "https://example.com/", Links = [new IpcLink { Url = "https://example.com/a" }], Cookies = [], UserAgent = "t" },
            new MediaDetected { Id = "5", TabId = 3, PageUrl = "https://example.com/", Kind = MediaKind.Direct, Url = "https://example.com/v.mp4" },
            new CaptureMedia { Id = "6", TabId = 3, Url = "https://example.com/v.m3u8", Kind = MediaKind.Hls, Cookies = [], Referrer = "https://example.com/", UserAgent = "t" },
            new RefreshLink { Id = "7", JobId = "job", ReferrerUrl = "https://example.com/" },
            new LinkRefreshed { Id = "8", JobId = "job", Url = "ftp://example.com/f", Cookies = [] },
            new OpenSettings { Id = "9", Section = SettingsSection.General },
            new Ack { Id = "10", Ok = true },
            new IpcError { Id = "11", Code = IpcErrorCode.AppNotRunning, Detail = "start Kasi" },
        ];

        foreach (var message in messages)
        {
            var back = IpcSerializer.Deserialize(IpcSerializer.Serialize(message));

            back.Should().BeOfType(message.GetType());
            IpcSerializer.Serialize(back).Should().Equal(IpcSerializer.Serialize(message));
        }

        messages.Select(m => m.GetType()).Should().OnlyHaveUniqueItems().And.HaveCount(11);
    }

    [Fact]
    public void The_type_may_come_last_and_optional_fields_may_be_left_out()
    {
        var message = IpcSerializer.Deserialize("""{"v":1,"id":"x","section":null,"type":"openSettings"}"""u8);

        message.Should().BeOfType<OpenSettings>().Which.Section.Should().BeNull("an absent section means the browser page");
    }

    [Fact]
    public void Optional_fields_are_not_written_when_they_are_empty()
    {
        var json = Encoding.UTF8.GetString(IpcSerializer.Serialize(new OpenSettings { Id = "x" }));

        json.Should().NotContain("section").And.Contain("\"type\":\"openSettings\"").And.Contain("\"v\":1");
    }

    [Theory]
    [InlineData("""{"id":"x","type":"ack","ok":true}""")]
    [InlineData("""{"v":1,"type":"ack","ok":true}""")]
    [InlineData("""{"v":1,"id":"x","ok":true}""")]
    [InlineData("""{"v":1,"id":"x","type":"ack","ok":true,"extra":1}""")]
    [InlineData("""{"v":1,"id":"x","type":"ack","ok":"yes"}""")]
    [InlineData("""{"v":1,"id":"x","type":"ack"}""")]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    public void Malformed_messages_are_rejected_as_invalid(string json) => ParseCode(json).Should().Be(IpcErrorCode.InvalidMessage);

    [Theory]
    [InlineData("""{"v":2,"id":"x","type":"ack","ok":true}""")]
    [InlineData("""{"v":0,"id":"x","type":"ack","ok":true}""")]
    [InlineData("""{"id":"x","type":"somethingNew","v":2,"fromTheFuture":true}""")]
    public void A_version_other_than_1_is_unsupported_even_when_the_rest_has_another_shape(string json) =>
        ParseCode(json).Should().Be(IpcErrorCode.UnsupportedVersion);

    [Fact]
    public void A_nested_unknown_field_is_rejected()
    {
        var json = """{"v":1,"id":"x","type":"linkRefreshed","jobId":"j","url":"https://e.example/f","cookies":[{"name":"a","value":"b","domain":"d","path":"/","secure":true,"httpOnly":true,"sameSite":"lax"}]}""";

        ParseCode(json).Should().Be(IpcErrorCode.InvalidMessage);
    }

    [Fact]
    public void A_rejected_message_never_puts_the_input_in_the_exception()
    {
        var json = """{"v":1,"id":"x","type":"addDownload","url":"https://example.com/f?token=s3cr3t","finalUrl":"nope","filename":"f","mime":"","userAgent":"","cookies":[]}""";

        var thrown = ((Action)(() => IpcSerializer.Deserialize(Encoding.UTF8.GetBytes(json)))).Should().Throw<IpcProtocolException>().Which;

        thrown.Message.Should().NotContain("s3cr3t").And.NotContain("token").And.NotContain("example");
        thrown.InnerException.Should().BeNull("a JSON error can quote a path or a value");
    }

    [Theory]
    [InlineData(IpcLimits.UrlLength, true)]
    [InlineData(IpcLimits.UrlLength + 1, false)]
    public void A_url_over_the_cap_is_rejected(int length, bool accepted)
    {
        var url = "https://example.com/" + new string('a', length - "https://example.com/".Length);
        var message = Download(url: url);

        if (accepted)
        {
            IpcSerializer.Deserialize(IpcSerializer.Serialize(message)).Should().BeOfType<AddDownload>();
        }
        else
        {
            Rejected(message);
        }
    }

    [Theory]
    [InlineData(IpcLimits.Cookies, true)]
    [InlineData(IpcLimits.Cookies + 1, false)]
    public void More_cookies_than_the_cap_are_rejected(int count, bool accepted)
    {
        var message = Download(cookies: [.. Enumerable.Repeat(_cookie, count)]);

        if (accepted)
        {
            IpcSerializer.Serialize(message).Should().NotBeEmpty();
        }
        else
        {
            Rejected(message);
        }
    }

    [Fact]
    public void A_cookie_value_over_the_cap_is_rejected()
    {
        Rejected(Download(cookies: [_cookie with { Value = new string('v', IpcLimits.CookieValueLength + 1) }]));
        IpcSerializer.Serialize(Download(cookies: [_cookie with { Value = new string('v', IpcLimits.CookieValueLength) }])).Should().NotBeEmpty();
    }

    [Fact]
    public void A_list_over_the_cap_or_with_a_long_entry_is_rejected()
    {
        Rules With(IReadOnlyList<string> extensions) => new()
        {
            Id = "r",
            Enabled = true,
            Extensions = extensions,
            MimeTypes = [],
            ExcludedSites = [],
            BypassKey = BypassKey.Alt,
            AckTimeoutMs = 2000,
        };

        IpcSerializer.Serialize(With([.. Enumerable.Repeat("zip", IpcLimits.ListEntries)])).Should().NotBeEmpty();
        Rejected(With([.. Enumerable.Repeat("zip", IpcLimits.ListEntries + 1)]));
        Rejected(With([new string('z', IpcLimits.ListEntryLength + 1)]));
    }

    [Fact]
    public void A_batch_needs_at_least_one_link_and_at_most_the_cap()
    {
        AddBatch With(int links) => new()
        {
            Id = "b",
            Referrer = "https://example.com/",
            Cookies = [],
            UserAgent = "t",
            Links = [.. Enumerable.Repeat(new IpcLink { Url = "https://example.com/a" }, links)],
        };

        Rejected(With(0));
        IpcSerializer.Serialize(With(IpcLimits.BatchLinks)).Should().NotBeEmpty();
        Rejected(With(IpcLimits.BatchLinks + 1));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/windows/system32/cmd.exe")]
    [InlineData("data:text/plain;base64,AAAA")]
    [InlineData("blob:https://example.com/1234")]
    [InlineData("//example.com/a")]
    [InlineData("example.com/a")]
    [InlineData("")]
    public void Only_http_https_ftp_and_ftps_urls_are_accepted_for_a_download(string url) => Rejected(Download(url: url));

    [Theory]
    [InlineData("ftp://example.com/a.iso")]
    [InlineData("ftps://example.com/a.iso")]
    [InlineData("http://127.0.0.1:8080/a")]
    public void The_download_schemes_are_accepted(string url) => IpcSerializer.Serialize(Download(url: url)).Should().NotBeEmpty();

    [Fact]
    public void Sending_a_message_with_another_version_is_refused_before_it_leaves()
    {
        var act = () => IpcSerializer.Serialize(new Ack { Id = "x", Ok = true, V = 2 });

        act.Should().Throw<IpcProtocolException>().Which.Code.Should().Be(IpcErrorCode.UnsupportedVersion);
    }

    [Fact]
    public void An_ack_cannot_say_it_failed()
    {
        Rejected(new Ack { Id = "x", Ok = false });
    }
}
