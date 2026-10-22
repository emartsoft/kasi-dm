using Avalonia.Headless.XUnit;
using Kasi.App.Browser;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Ipc.Messages;

namespace Kasi.App.Tests;

internal sealed class RecordingSink : IBrowserRequestSink
{
    public List<BrowserDownload> Added { get; } = [];

    public List<BrowserBatch> Pickers { get; } = [];

    public void ShowAdd(BrowserDownload download) => Added.Add(download);

    public void ShowPicker(BrowserBatch batch) => Pickers.Add(batch);
}

[Trait("Requirement", "KASI-BRW-005")]
public class BrowserIntakeTests
{
    private static BrowserDownload Download(string name) => new("https://example.com/" + name, "https://example.com/" + name, null, name, "application/zip", "t", []);

    [Fact]
    public void Requests_that_arrive_before_the_ui_wait_and_are_delivered_in_order_on_attach()
    {
        var intake = new BrowserIntake();
        intake.TryAdd(Download("1")).Should().BeTrue();
        intake.TryAdd(Download("2")).Should().BeTrue();
        intake.TryAddBatch(new BrowserBatch("https://example.com/", [], [], "t")).Should().BeTrue();
        var sink = new RecordingSink();
        var posted = 0;

        intake.Attach(sink, action =>
        {
            posted++;
            action();
        });
        intake.TryAdd(Download("3")).Should().BeTrue();

        sink.Added.Select(d => d.Filename).Should().Equal("1", "2", "3");
        sink.Pickers.Should().HaveCount(1);
        posted.Should().Be(4, "every delivery is moved to the UI thread by the supplied post");
        intake.PendingDownloads.Should().Be(0);
    }

    [Fact]
    public void A_full_queue_refuses_so_a_misbehaving_browser_cannot_make_kasi_hold_unlimited_requests()
    {
        var intake = new BrowserIntake();
        for (var i = 0; i < BrowserIntake.MaxPendingDownloads; i++)
        {
            intake.TryAdd(Download("d")).Should().BeTrue();
        }

        intake.TryAdd(Download("one-too-many")).Should().BeFalse();
        for (var i = 0; i < BrowserIntake.MaxPendingBatches; i++)
        {
            intake.TryAddBatch(new BrowserBatch("https://example.com/", [], [], "t")).Should().BeTrue();
        }

        intake.TryAddBatch(new BrowserBatch("https://example.com/", [], [], "t")).Should().BeFalse();
    }

    [Fact]
    public void The_settings_window_request_that_comes_first_waits_for_the_ui_and_only_the_latest_section_counts()
    {
        var launcher = new SettingsLauncher();
        launcher.Show(SettingsSection.General);
        launcher.Show(SettingsSection.Browser);
        var shown = new List<SettingsSection>();

        launcher.Attach(shown.Add);
        launcher.Show(SettingsSection.General);

        shown.Should().Equal(SettingsSection.Browser, SettingsSection.General);
    }
}

[Trait("Requirement", "KASI-BRW-002")]
public class SettingsViewModelTests
{
    [Fact]
    public async Task The_form_round_trips_the_settings_and_saving_cleans_and_stores_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new FixedSettings(new BrowserSettings
        {
            Extensions = ["iso"],
            MimeTypes = ["video/*"],
            ExcludedSites = ["bank.example"],
            BypassKey = BypassKey.Shift,
            MinSizeBytes = 5L * 1024 * 1024,
        });
        var model = new SettingsViewModel(store);

        model.ExtensionsText.Should().Be("iso");
        model.SelectedBypassKey.Key.Should().Be(BypassKey.Shift);
        model.MinSizeMegabytes.Should().Be(5);

        model.ExtensionsText = " .ZIP,\r\n7z ; iso\n";
        model.MinSizeMegabytes = 0;
        var saved = false;
        model.Saved += () => saved = true;
        await model.SaveCommand.ExecuteAsync(null);

        saved.Should().BeTrue();
        store.Current.Extensions.Should().Equal("zip", "7z", "iso");
        store.Current.MinSizeBytes.Should().BeNull("0 megabytes means any size");
        ct.IsCancellationRequested.Should().BeFalse();
    }

    private sealed class FailingStore : IBrowserSettings
    {
        public BrowserSettings Current { get; } = new();

        public event Action<BrowserSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken) => throw new IOException("disk full");
    }

    [Fact]
    public async Task A_failed_save_says_so_and_keeps_the_window_open()
    {
        var model = new SettingsViewModel(new FailingStore());
        var saved = false;
        model.Saved += () => saved = true;

        await model.SaveCommand.ExecuteAsync(null);

        saved.Should().BeFalse();
        model.StatusText.Should().NotBeEmpty();
    }

    [AvaloniaFact]
    [Trait("Requirement", "KASI-UI-010")]
    public void The_settings_window_opens_on_the_asked_tab_and_every_control_has_an_automation_name()
    {
        var model = new SettingsViewModel(new FixedSettings(new BrowserSettings()));
        var window = new SettingsWindow { DataContext = model };
        window.Show();

        model.ShowSection(SettingsSection.Browser);

        window.IsVisible.Should().BeTrue();
        Avalonia.Automation.AutomationProperties.GetName(window).Should().Be("Kasi settings window");
        model.SelectedTab.Should().Be(1);
        model.ShowSection(SettingsSection.General);
        model.SelectedTab.Should().Be(0);
        window.Close();
    }
}
