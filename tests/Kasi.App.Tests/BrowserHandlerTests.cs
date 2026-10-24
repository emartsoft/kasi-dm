using Kasi.App.Browser;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Microsoft.Extensions.Time.Testing;

namespace Kasi.App.Tests;

internal sealed class FakeIntake : IBrowserIntake
{
    public List<BrowserDownload> Downloads { get; } = [];

    public List<BrowserBatch> Batches { get; } = [];

    public bool Accept { get; set; } = true;

    public bool TryAdd(BrowserDownload download)
    {
        if (Accept)
        {
            Downloads.Add(download);
        }

        return Accept;
    }

    public bool TryAddBatch(BrowserBatch batch)
    {
        if (Accept)
        {
            Batches.Add(batch);
        }

        return Accept;
    }
}

internal sealed class FakeMediaCapture : IBrowserMediaCapture
{
    public MediaCaptureResult Result { get; set; } = new(MediaCaptureStatus.Accepted);

    public List<CaptureMedia> Captured { get; } = [];

    public Task<MediaCaptureResult> CaptureAsync(CaptureMedia message, CancellationToken cancellationToken)
    {
        Captured.Add(message);
        return Task.FromResult(Result);
    }
}

internal sealed class FixedSettings(BrowserSettings initial) : IBrowserSettings
{
    public BrowserSettings Current { get; private set; } = initial.Normalized();

    public event Action<BrowserSettings>? Changed;

    public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken)
    {
        Current = settings.Normalized();
        Changed?.Invoke(Current);
        return Task.CompletedTask;
    }
}

internal sealed class FakeSettingsLauncher : ISettingsLauncher
{
    public List<SettingsSection> Shown { get; } = [];

    public void Show(SettingsSection section) => Shown.Add(section);
}

/// <summary>The handler behind a real <see cref="KasiPipeServer"/> on a private pipe, with a connected client: the framing and the access list are the real ones.</summary>
internal sealed class PipeRig : IAsyncDisposable
{
    private static readonly TimeSpan _connect = TimeSpan.FromSeconds(10);

    private PipeRig(KasiPipeServer server, KasiPipeClient client, BrowserMessageHandler handler)
    {
        Server = server;
        Client = client;
        Handler = handler;
    }

    public KasiPipeServer Server { get; }

    public KasiPipeClient Client { get; }

    public BrowserMessageHandler Handler { get; }

    /// <summary>Starts the pipe and, unless told not to, plays the extension's first step: a <c>hello</c> (the server refuses anything else first).</summary>
    public static async Task<PipeRig> StartAsync(BrowserMessageHandler handler, CancellationToken ct, bool sayHello = true)
    {
        var endpoints = TestEndpoints.New();
#pragma warning disable CA2000 // owned by the rig, disposed with it
        var server = new KasiPipeServer(endpoints.BrowserPipe, handler.Handler);
#pragma warning restore CA2000
        server.Start();
        var client = await KasiPipeClient.ConnectAsync(endpoints.BrowserPipe, _connect, ct);
        if (sayHello)
        {
            await client.SendAsync(new Hello { Id = "h0", Client = BrowserClient.Chrome, ExtensionVersion = "0.2.0", ProtocolMin = 1, ProtocolMax = 1 }, ct);
            await client.ReceiveAsync(ct).WaitAsync(_connect, ct);
        }

        return new PipeRig(server, client, handler);
    }

    public async Task<IpcMessage> AskAsync(IpcMessage message, CancellationToken ct)
    {
        await Client.SendAsync(message, ct);
        return await Client.ReceiveAsync(ct).WaitAsync(_connect, ct) ?? throw new InvalidOperationException("The server closed the pipe.");
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Server.DisposeAsync();
    }
}

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-BRW-002")]
public class BrowserHandlerTests
{
    private static readonly string[] _isoAnd7z = ["iso", "7z"];
    private static readonly string[] _video = ["video/*"];
    private static readonly string[] _bank = ["bank.example"];

    private const string CanaryUrlToken = "CANARY-URL-TOKEN-4c2e";
    private const string CanaryCookie = "CANARY-COOKIE-VALUE-8b71";

    private static Hello Hello(string version = "0.2.0") =>
        new() { Id = "h1", Client = BrowserClient.Chrome, ExtensionVersion = version, ProtocolMin = 1, ProtocolMax = 1 };

    private static AddDownload Add(string id, string url = "https://example.com/files/a.zip") => new()
    {
        Id = id,
        Url = url,
        FinalUrl = url,
        Referrer = "https://example.com/",
        Filename = "a.zip",
        Mime = "application/zip",
        UserAgent = "TestAgent/1.0",
        Cookies = [new IpcCookie { Name = "sid", Value = CanaryCookie, Domain = ".example.com", Path = "/", Secure = true, HttpOnly = true }],
    };

    private static BrowserMessageHandler HandlerFor(
        BrowserSettings? settings,
        out FakeIntake intake,
        out FakeSettingsLauncher launcher,
        TimeProvider? time = null,
        CapturingLogger? logger = null,
        BrowserMediaServices? media = null)
    {
        intake = new FakeIntake();
        launcher = new FakeSettingsLauncher();
        return new BrowserMessageHandler(new FixedSettings(settings ?? new BrowserSettings()), intake, launcher, time, logger, media: media);
    }

    [Fact]
    [Trait("TC", "TC-BRW-002")]
    public async Task Hello_is_answered_with_rules_built_from_the_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        var settings = new BrowserSettings
        {
            Enabled = true,
            Extensions = ["iso", "7z"],
            MimeTypes = ["video/*"],
            ExcludedSites = ["bank.example"],
            BypassKey = BypassKey.Shift,
            AckTimeoutMs = 3000,
        };
        await using var rig = await PipeRig.StartAsync(HandlerFor(settings, out _, out _), ct, sayHello: false);

        var reply = await rig.AskAsync(Hello(), ct);

        reply.Should().BeOfType<Rules>().Which.Should().Match<Rules>(r =>
            r.Id == "h1" && r.Enabled && r.Extensions.SequenceEqual(_isoAnd7z) && r.MimeTypes.SequenceEqual(_video)
            && r.ExcludedSites.SequenceEqual(_bank) && r.BypassKey == BypassKey.Shift && r.AckTimeoutMs == 3000);
    }

    [Theory]
    [InlineData("0.1.9", false)]
    [InlineData("0.2.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("not-a-version", false)]
    [Trait("TC", "TC-BRW-002")]
    public async Task The_minimum_size_goes_only_to_an_extension_that_can_read_it(string version, bool sent)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await PipeRig.StartAsync(HandlerFor(new BrowserSettings { MinSizeBytes = 5_000_000 }, out _, out _), ct, sayHello: false);

        var reply = await rig.AskAsync(Hello(version), ct);

        reply.Should().BeOfType<Rules>().Which.MinSizeBytes.Should().Be(sent ? 5_000_000 : null);
    }

    [Fact]
    [Trait("TC", "TC-BRW-002")]
    public async Task An_added_download_is_acknowledged_once_it_is_queued_and_its_fields_arrive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out var intake, out _), ct);

        var reply = await rig.AskAsync(Add("a1"), ct);

        reply.Should().BeOfType<Ack>().Which.Should().Match<Ack>(a => a.Id == "a1" && a.Ok);
        var queued = intake.Downloads.Should().ContainSingle().Subject;
        queued.Url.Should().Be("https://example.com/files/a.zip");
        queued.Referrer.Should().Be("https://example.com/");
        queued.Cookies.Should().ContainSingle().Which.Value.Should().Be(CanaryCookie);
    }

    [Fact]
    [Trait("TC", "TC-BRW-002")]
    public async Task The_same_request_id_twice_is_acknowledged_twice_and_queued_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out var intake, out _), ct);

        var first = await rig.AskAsync(Add("dup"), ct);
        var second = await rig.AskAsync(Add("dup"), ct);

        first.Should().BeOfType<Ack>();
        second.Should().BeOfType<Ack>().Which.Id.Should().Be("dup");
        intake.Downloads.Should().HaveCount(1);
    }

    [Fact]
    [Trait("TC", "TC-BRW-002")]
    public async Task A_remembered_id_expires_so_the_memory_is_bounded_in_time_and_in_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new FakeTimeProvider();
        var handler = HandlerFor(null, out var intake, out _, time);
        await using var rig = await PipeRig.StartAsync(handler, ct);

        await rig.AskAsync(Add("old"), ct);
        time.Advance(BrowserMessageHandler.RememberFor + TimeSpan.FromSeconds(1));
        await rig.AskAsync(Add("old"), ct);

        intake.Downloads.Should().HaveCount(2, "after the window the same id is a new request");

        for (var i = 0; i < BrowserMessageHandler.RememberedRequests + 200; i++)
        {
            await rig.AskAsync(Add("id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), ct);
        }

        handler.RememberedCount.Should().BeLessThanOrEqualTo(BrowserMessageHandler.RememberedRequests);
    }

    [Fact]
    [Trait("TC", "TC-BRW-002")]
    public async Task A_refused_request_gets_an_error_and_is_not_remembered_so_a_retry_can_succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out var intake, out _), ct);
        intake.Accept = false;

        var refused = await rig.AskAsync(Add("r1"), ct);
        intake.Accept = true;
        var retried = await rig.AskAsync(Add("r1"), ct);

        refused.Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.Internal);
        retried.Should().BeOfType<Ack>();
        intake.Downloads.Should().HaveCount(1);
    }

    [Fact]
    [Trait("TC", "TC-BRW-005")]
    public async Task A_link_batch_is_queued_for_the_picker_and_acknowledged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out var intake, out _), ct);
        var links = Enumerable.Range(0, 40).Select(i => new IpcLink { Url = "https://example.com/doc" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".pdf" }).ToList();

        var reply = await rig.AskAsync(
            new AddBatch { Id = "b1", Referrer = "https://example.com/", Links = links, Cookies = [], UserAgent = "TestAgent/1.0" },
            ct);

        reply.Should().BeOfType<Ack>().Which.Id.Should().Be("b1");
        intake.Batches.Should().ContainSingle().Which.Links.Should().HaveCount(40);
    }

    [Fact]
    [Trait("TC", "TC-BRW-006")]
    public async Task Open_settings_shows_the_asked_section_and_defaults_to_the_browser_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out _, out var launcher), ct);

        var general = await rig.AskAsync(new OpenSettings { Id = "o1", Section = SettingsSection.General }, ct);
        var bare = await rig.AskAsync(new OpenSettings { Id = "o2" }, ct);

        general.Should().BeOfType<Ack>().Which.Id.Should().Be("o1");
        bare.Should().BeOfType<Ack>().Which.Id.Should().Be("o2");
        launcher.Shown.Should().Equal(SettingsSection.General, SettingsSection.Browser);
    }

    [Fact]
    public async Task Media_detections_are_catalogued_and_acknowledged_and_a_capture_without_a_service_is_an_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var media = new BrowserMediaServices();
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out _, out _, media: media), ct);

        var detected = await rig.AskAsync(
            new MediaDetected
            {
                Id = "md",
                TabId = 1,
                PageUrl = "https://example.com/",
                Kind = MediaKind.Hls,
                Url = "https://example.com/a.m3u8",
                ThumbnailUrl = "https://example.com/poster.jpg",
                ContentType = "application/vnd.apple.mpegurl",
            },
            ct);
        var capture = await rig.AskAsync(
            new CaptureMedia { Id = "cm", TabId = 1, Url = "https://example.com/a.m3u8", Kind = MediaKind.Hls, Cookies = [], Referrer = "https://example.com/", UserAgent = "t" },
            ct);
        var wrongWay = await rig.AskAsync(
            new Rules { Id = "rw", Enabled = true, Extensions = [], MimeTypes = [], ExcludedSites = [], BypassKey = BypassKey.Alt, AckTimeoutMs = 2000 },
            ct);

        detected.Should().BeOfType<Ack>().Which.Id.Should().Be("md");
        media.Catalog.ForTab(1).Should().ContainSingle().Which.ThumbnailUrl.Should().Be("https://example.com/poster.jpg");
        capture.Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.Internal);
        wrongWay.Should().BeOfType<IpcError>().Which.Code.Should().Be(IpcErrorCode.InvalidMessage);
    }

    [Fact]
    public async Task A_protected_capture_is_acknowledged_with_the_exact_message_and_a_clear_one_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var media = new BrowserMediaServices
        {
            Capture = new FakeMediaCapture
            {
                Result = new MediaCaptureResult(MediaCaptureStatus.Protected, "This video is protected and can't be saved"),
            },
        };
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out _, out _, media: media), ct);
        var capture = new CaptureMedia
        {
            Id = "p1",
            TabId = 1,
            Url = "https://example.com/a.m3u8",
            Kind = MediaKind.Hls,
            Cookies = [],
            Referrer = "https://example.com/",
            UserAgent = "t",
        };

        var refused = await rig.AskAsync(capture, ct);

        refused.Should().BeOfType<Ack>().Which.Detail.Should().Be("This video is protected and can't be saved");
        media.Capture = new FakeMediaCapture { Result = new MediaCaptureResult(MediaCaptureStatus.Accepted) };
        var accepted = await rig.AskAsync(capture with { Id = "p2" }, ct);
        accepted.Should().BeOfType<Ack>().Which.Ok.Should().BeTrue();
    }

    [Fact]
    [Trait("TC", "TC-BRW-006")]
    public async Task A_settings_change_is_pushed_to_every_connected_extension_with_its_own_minimum_size_rule()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = TestBrowser.New();
        var endpoints = TestEndpoints.New();
        await using var server = new KasiPipeServer(endpoints.BrowserPipe, services.Handler.Handler);
        server.Start();
        using var publisher = services.PublishChanges(server);
        await using var current = await KasiPipeClient.ConnectAsync(endpoints.BrowserPipe, TimeSpan.FromSeconds(10), ct);
        await using var old = await KasiPipeClient.ConnectAsync(endpoints.BrowserPipe, TimeSpan.FromSeconds(10), ct);
        await current.SendAsync(Hello("0.2.0"), ct);
        await old.SendAsync(Hello("0.1.0"), ct);
        await current.ReceiveAsync(ct);
        await old.ReceiveAsync(ct);

        await services.Settings.SaveAsync(new BrowserSettings { Extensions = ["iso"], MimeTypes = [], MinSizeBytes = 1_000_000, BypassKey = BypassKey.None }, ct);

        var pushedToCurrent = (Rules)(await current.ReceiveAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct))!;
        var pushedToOld = (Rules)(await old.ReceiveAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct))!;
        pushedToCurrent.Extensions.Should().Equal("iso");
        pushedToCurrent.BypassKey.Should().Be(BypassKey.None);
        pushedToCurrent.MinSizeBytes.Should().Be(1_000_000);
        pushedToOld.MinSizeBytes.Should().BeNull("an extension below 0.2.0 would refuse the whole message");
    }

    [Fact]
    [Trait("Requirement", "KASI-NFR-013")]
    public async Task Nothing_the_browser_sent_reaches_the_log()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new CapturingLogger();
        await using var rig = await PipeRig.StartAsync(HandlerFor(null, out _, out _, logger: logger), ct);

        await rig.AskAsync(Add("l1", "https://example.com/a.zip?token=" + CanaryUrlToken), ct);
        await rig.AskAsync(new AddBatch { Id = "l2", Referrer = "https://example.com/?x=" + CanaryUrlToken, Links = [new IpcLink { Url = "https://example.com/z.zip?t=" + CanaryUrlToken }], Cookies = [], UserAgent = "t" }, ct);

        logger.Lines.Should().NotBeEmpty();
        logger.Lines.Should().NotContain(line => line.Contains(CanaryUrlToken, StringComparison.Ordinal) || line.Contains(CanaryCookie, StringComparison.Ordinal));
        new BrowserDownload("https://x/y", "https://x/y", null, "f", "m", "u", [])
            .ToString().Should().NotContain("https", "a download holds a URL and cookies, so it prints only its type");
    }

    [Fact]
    public void Settings_are_cleaned_to_the_protocol_caps_whatever_was_typed()
    {
        var messy = new BrowserSettings
        {
            Extensions = [" .ZIP ", "zip", "", "7Z", .. Enumerable.Range(0, 2000).Select(i => "e" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            MimeTypes = ["Video/*", new string('x', IpcLimits.ListEntryLength + 1)],
            ExcludedSites = [".Example.COM"],
            AckTimeoutMs = 5,
            MinSizeBytes = -3,
        };

        var clean = messy.Normalized();

        clean.Extensions.Take(2).Should().Equal("zip", "7z");
        clean.Extensions.Should().HaveCount(IpcLimits.ListEntries).And.OnlyHaveUniqueItems();
        clean.MimeTypes.Should().Equal("video/*");
        clean.ExcludedSites.Should().Equal("example.com");
        clean.AckTimeoutMs.Should().Be(IpcLimits.AckTimeoutMinMs);
        clean.MinSizeBytes.Should().Be(0);
    }

    [Fact]
    public async Task The_default_settings_are_populated_because_an_empty_list_intercepts_nothing()
    {
        var defaults = new BrowserSettings().Normalized();
        defaults.Extensions.Should().NotBeEmpty().And.Contain("zip").And.Contain("iso");
        defaults.MimeTypes.Should().NotBeEmpty();
        defaults.Enabled.Should().BeTrue();

        // and they survive being written and read back unchanged
        var store = TestBrowser.NewStore();
        await store.SaveAsync(defaults, TestContext.Current.CancellationToken);
        new BrowserSettingsStore(store.FilePath).Current.Should().BeEquivalentTo(defaults);
    }
}
