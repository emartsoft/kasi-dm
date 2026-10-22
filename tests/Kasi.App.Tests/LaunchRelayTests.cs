using Kasi.App.Startup;

namespace Kasi.App.Tests;

[Trait("TC", "TC-SYS-006")]
[Trait("Requirement", "KASI-SYS-005")]
public class LaunchRelayTests
{
    [Fact]
    public void A_request_that_comes_before_the_ui_waits_and_is_delivered_in_order_on_attach()
    {
        var relay = new LaunchRelay();
        relay.Submit(AppArguments.Parse(["https://a.example/1"]));
        relay.Submit(AppArguments.Parse(["https://a.example/2"]));
        var seen = new List<string>();

        relay.Attach(request => seen.AddRange(request.Urls));
        relay.Submit(AppArguments.Parse(["https://a.example/3"]));

        seen.Should().Equal("https://a.example/1", "https://a.example/2", "https://a.example/3");
    }

    [Fact]
    public async Task A_hand_off_is_parsed_with_the_same_rules_and_never_logged_with_its_urls()
    {
        var logger = new CapturingLogger();
        var relay = new LaunchRelay(logger);
        AppArguments? got = null;
        relay.Attach(request => got = request);

        await relay.OnHandOffAsync(["--tray", "https://example.com/a.zip?token=SECRETVALUE", "file:///C:/Users/someone/x", "--other"], TestContext.Current.CancellationToken);

        got.Should().NotBeNull();
        got!.Tray.Should().BeTrue();
        got.Urls.Should().Equal("https://example.com/a.zip?token=SECRETVALUE");
        got.Rejected.Should().Be(2);
        logger.Lines.Should().ContainSingle().Which.Should().Contain("1 urls").And.Contain("2 rejected");
        logger.Lines.Should().NotContain(line => line.Contains("SECRETVALUE") || line.Contains("example.com") || line.Contains("Users"));
    }
}
