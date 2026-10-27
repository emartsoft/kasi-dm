using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.ViewModels;
using Kasi.App.Views;

namespace Kasi.App.Tests;

/// <summary>UIR-4 (docs/plans/ui-redesign.md): the Settings window's navigation, search, Reset and Restore Defaults, and the This PC card. KASI-UI-006.</summary>
[Trait("Requirement", "KASI-UI-006")]
public class SettingsShellTests
{
    private sealed class Store(BrowserSettings current) : IBrowserSettings
    {
        public BrowserSettings Current { get; private set; } = current;

#pragma warning disable CS0067
        public event Action<BrowserSettings>? Changed;
#pragma warning restore CS0067

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken)
        {
            Current = settings;
            return Task.CompletedTask;
        }
    }

    private static (SettingsViewModel Model, ShellSpy Shell, List<string> Log) Open(BrowserSettings? settings = null, int hosts = 0)
    {
        var shell = new ShellSpy();
        var log = new List<string>();
        var environment = new SettingsEnvironment(@"C:\Users\someone\AppData\Local\Kasi", "1.4.2", () => hosts, () => log.Add("scheduler"), shell);
        return (new SettingsViewModel(new Store(settings ?? new BrowserSettings()), null, null, environment), shell, log);
    }

    [Fact]
    public void The_navigation_lists_the_pages_that_have_content_and_the_list_and_the_page_number_are_one_thing()
    {
        var (model, _, _) = Open();

        model.Pages.Select(p => p.Label).Should().Equal("General", "Browser Integration", "Scheduler & Queues", "About");
        model.IsGeneralPage.Should().BeTrue();

        model.SelectedPage = model.Pages.Single(p => p.Id == 1);

        model.SelectedTab.Should().Be(1);
        model.IsBrowserPage.Should().BeTrue();
        model.Breadcrumb.Should().Be("Settings / Browser Integration");
        model.PageTitle.Should().Be("Browser Integration");
        model.ShowSection(Kasi.Ipc.Messages.SettingsSection.General);
        model.SelectedPage!.Id.Should().Be(0, "asking for a page from outside moves the list");
    }

    [Fact]
    public void Find_a_setting_matches_labels_on_any_page_and_choosing_one_opens_its_page()
    {
        var (model, _, _) = Open();

        model.SearchText = "when I copy";

        model.HasHits.Should().BeTrue();
        var hit = model.Hits.Should().ContainSingle().Subject;
        hit.PageId.Should().Be(1);
        model.OpenHitCommand.Execute(hit);
        model.SelectedTab.Should().Be(1);
        model.SearchText.Should().BeEmpty();
        model.HasHits.Should().BeFalse();
        model.SearchText = "ABOUT";
        model.Hits.Select(h => h.PageId).Should().Equal(4);
        model.SearchText = "zzzz";
        model.Hits.Should().BeEmpty();
    }

    [Fact]
    public void Reset_puts_one_list_back_and_Restore_Defaults_puts_the_whole_form_back_without_saving()
    {
        var custom = new BrowserSettings { Enabled = false, ClipboardCapture = true, Extensions = ["abc"], MimeTypes = ["x/y"], ExcludedSites = ["example.com"] };
        var (model, _, _) = Open(custom);
        model.ExtensionsText.Should().Be("abc");

        model.ResetExtensionsCommand.Execute(null);
        model.ExtensionsText.Should().Contain("zip").And.Contain("iso");
        model.MimeTypesText.Should().Be("x/y", "Reset touches only its own list");
        model.ResetExcludedSitesCommand.Execute(null);
        model.ExcludedSitesText.Should().BeEmpty();

        model.RestoreDefaultsCommand.Execute(null);

        model.Enabled.Should().BeTrue();
        model.ClipboardCapture.Should().BeFalse();
        model.MimeTypesText.Should().Contain("application/zip");
        model.SelectedTheme.Preference.Should().Be(Kasi.App.Engine.ThemePreference.System);
    }

    [Fact]
    public void The_This_PC_card_shortens_the_data_folder_shows_the_version_and_opens_the_folder_and_the_scheduler()
    {
        var (model, shell, log) = Open(hosts: 1);

        model.DataFolderText.Should().Be(string.Join(Path.DirectorySeparatorChar, "...", "Local", "Kasi"), "not the whole path of a user folder");
        model.VersionText.Should().Be("1.4.2");
        model.ExtensionText.Should().Be("Connected");
        model.OpenDataFolderCommand.Execute(null);
        model.OpenSchedulerCommand.Execute(null);

        shell.Folders.Should().Equal(@"C:\Users\someone\AppData\Local\Kasi");
        log.Should().Equal("scheduler");
        SettingsViewModel.ShortFolder("Kasi").Should().Be("Kasi");
    }

    [AvaloniaFact]
    public void Every_page_shows_its_controls_with_names_and_only_the_current_page_is_visible()
    {
        var (model, _, _) = Open();
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        var unnamed = new List<string>();

        foreach (var page in model.Pages)
        {
            model.SelectedPage = page;
            window.UpdateLayout();
            foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is Button or ComboBox or ToggleSwitch or NumericUpDown))
            {
                if (string.IsNullOrEmpty(Avalonia.Automation.AutomationProperties.GetName(control)) && !control.GetVisualAncestors().OfType<NumericUpDown>().Any())
                {
                    unnamed.Add(page.Label + ": " + control.GetType().Name);
                }
            }
        }

        unnamed.Should().BeEmpty();
        model.SelectedTab = 1;
        window.UpdateLayout();
        var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => Avalonia.Automation.AutomationProperties.GetName(b)).ToList();
        buttons.Should().Contain(["Reset " + model.ExtensionsLabel, "Restore Defaults", "Save", "Cancel"]);
        window.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.IsEffectivelyVisible).Should().HaveCount(3);
        model.SelectedTab = 0;
        window.UpdateLayout();
        window.GetVisualDescendants().OfType<ToggleSwitch>().Where(t => t.IsEffectivelyVisible).Should().HaveCount(2, "General has the scan and Start with Windows switches: the Browser page is not shown there");
        window.Close();
    }

    [Fact]
    [Trait("TC", "TC-CAT-004")]
    public async Task Scanning_downloads_is_off_by_default_is_saved_with_the_settings_and_Restore_Defaults_turns_it_off()
    {
        var ui = new MemoryUiSettings();
        var model = new SettingsViewModel(new Store(new BrowserSettings()), ui);
        model.ScanDownloads.Should().BeFalse("off is the default");
        ui.Current.ScanDownloads.Should().BeFalse();

        model.ScanDownloads = true;
        await model.SaveCommand.ExecuteAsync(null);

        ui.Current.ScanDownloads.Should().BeTrue();
        model.SearchText = "antivirus";
        model.Hits.Should().ContainSingle().Which.PageId.Should().Be(0);
        model.RestoreDefaultsCommand.Execute(null);
        model.ScanDownloads.Should().BeFalse();
    }

    [Fact]
    [Trait("TC", "TC-CAT-004")]
    public async Task The_engines_scanner_follows_the_setting_at_each_completion_and_scans_through_the_real_one_only_when_on()
    {
        var ui = new MemoryUiSettings();
        var inner = new CountingScanner();
        var gated = new SettingGatedScanner(ui, inner);

        gated.IsEnabled.Should().BeFalse();
        await ui.SaveAsync(ui.Current with { ScanDownloads = true }, TestContext.Current.CancellationToken);
        gated.IsEnabled.Should().BeTrue("the setting is read each time, not once at start-up");
        (await gated.ScanAsync("a.bin", TestContext.Current.CancellationToken)).Verdict.Should().Be(Kasi.Core.ScanVerdict.Clean);
        inner.Calls.Should().Be(1);
    }

    private sealed class CountingScanner : Kasi.Core.IFileScanner
    {
        public int Calls { get; private set; }

        public bool IsEnabled => true;

        public Task<Kasi.Core.ScanResult> ScanAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Kasi.Core.ScanResult.Clean);
        }
    }
}
