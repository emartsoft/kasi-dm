using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Kasi.App.Browser;
using Kasi.App.Notifications;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Core;
using Kasi.Engine;

namespace Kasi.App.Tests;

/// <summary>UIR-6 (docs/plans/ui-redesign.md): the same windows in the light and the dark theme, every interactive control named and reachable by keyboard. KASI-UI-006, KASI-UI-010.</summary>
[Trait("Requirement", "KASI-UI-010")]
public class AccessibilitySweepTests
{
    public static TheoryData<string> Themes => ["Light", "Dark"];

    private sealed class Store(BrowserSettings current) : IBrowserSettings
    {
        public BrowserSettings Current { get; } = current;

#pragma warning disable CS0067
        public event Action<BrowserSettings>? Changed;
#pragma warning restore CS0067

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static IEnumerable<string> Problems(Window window, string what)
    {
        window.UpdateLayout();
        // the parts inside a spinner, a drop-down or a list item belong to that control
        var interactive = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c is Button or TextBox or ComboBox or CheckBox or ToggleSwitch or NumericUpDown or ListBox or DataGrid or Slider)
            .Where(c => c.FindAncestorOfType<NumericUpDown>() is null && c.FindAncestorOfType<ComboBox>() is null && c.FindAncestorOfType<DataGridColumnHeader>() is null
                && c.FindAncestorOfType<ScrollBar>() is null && c.FindAncestorOfType<Expander>() is not { IsExpanded: false } && c is not RepeatButton);
        foreach (var control in interactive)
        {
            var name = AutomationProperties.GetName(control);
            if (string.IsNullOrEmpty(name) && control is not CheckBox { Content: string } && control is not Button { Content: string })
            {
                yield return $"{what}: {control.GetType().Name} has no automation name";
            }

            if (control is not ListBox and not DataGrid && !control.Focusable)
            {
                yield return $"{what}: {control.GetType().Name} '{name}' cannot take keyboard focus";
            }
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_main_window_the_details_window_and_the_add_dialog_name_and_focus_every_control_in_both_themes(string theme)
    {
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var job = TestJobs.New("ubuntu.iso", JobState.Downloading, total: 6_000_000_000, done: 3_000_000_000);
        await using var rig = await MainRig.CreateAsync(job);
        var problems = new List<string>();

        var main = new MainWindow { DataContext = rig.Model, RequestedThemeVariant = variant };
        main.Show();
        problems.AddRange(Problems(main, theme + " main window"));
        main.Close();

        var row = new JobRowViewModel(job);
        using var details = new DetailsViewModel(row, rig.Service, new ShellSpy(), action => action());
        await details.StartAsync(TestContext.Current.CancellationToken);
        var detailsWindow = new DetailsWindow { DataContext = details, RequestedThemeVariant = variant };
        detailsWindow.Show();
        problems.AddRange(Problems(detailsWindow, theme + " details window"));
        detailsWindow.Close();

        var add = new AddDownloadViewModel(new AddDownloadDraft("https://files.example.org/pack.zip"), new RecordingJobService(), new MemorySites(), _ => Task.FromResult<DuplicateAction?>(null));
        await add.LoadAsync(TestContext.Current.CancellationToken);
        var addWindow = new AddDownloadWindow { DataContext = add, RequestedThemeVariant = variant };
        addWindow.Show();
        problems.AddRange(Problems(addWindow, theme + " add dialog"));
        addWindow.Close();

        problems.Should().BeEmpty();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void Every_settings_page_names_and_focuses_every_control_in_both_themes(string theme)
    {
        var shell = new ShellSpy();
        var environment = new SettingsEnvironment(@"C:\Users\someone\AppData\Local\Kasi", "1.0", () => 0, () => { }, shell);
        var model = new SettingsViewModel(new Store(new BrowserSettings()), null, null, environment);
        var window = new SettingsWindow { DataContext = model, RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark };
        window.Show();
        var problems = new List<string>();

        foreach (var page in model.Pages)
        {
            model.SelectedPage = page;
            problems.AddRange(Problems(window, $"{theme} settings / {page.Label}"));
        }

        problems.Should().BeEmpty();
        window.Close();
    }
}
