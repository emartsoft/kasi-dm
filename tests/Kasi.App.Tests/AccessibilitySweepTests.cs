using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Tray;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Core;
using Kasi.Engine;
using Kasi.Ipc.Messages;
using Kasi.Media;

namespace Kasi.App.Tests;

/// <summary>UIR-6 (docs/plans/ui-redesign.md): the same windows in the light and the dark theme, every interactive control named and reachable by keyboard. KASI-UI-006, KASI-UI-010.</summary>
[Trait("Requirement", "KASI-UI-006")]
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

    private sealed class FakeRefresher : ILinkRefresher
    {
        public Task<RefreshOutcome> RefreshAsync(JobId id, CancellationToken cancellationToken) =>
            Task.FromResult(RefreshOutcome.Refreshed);
    }

    private sealed class FakeApp : IAppLifetime
    {
        public void ShowMainWindow() { }
        public void Quit() { }
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

    private static List<(string Type, string Name)> NarratorInventory(Window window)
    {
        window.UpdateLayout();
        var interactive = window.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c is Button or TextBox or ComboBox or CheckBox or ToggleSwitch or NumericUpDown or ListBox or DataGrid or Slider)
            .Where(c => c.FindAncestorOfType<NumericUpDown>() is null && c.FindAncestorOfType<ComboBox>() is null && c.FindAncestorOfType<DataGridColumnHeader>() is null
                && c.FindAncestorOfType<ScrollBar>() is null && c.FindAncestorOfType<Expander>() is not { IsExpanded: false } && c is not RepeatButton);

        var list = new List<(string Type, string Name)>();
        foreach (var control in interactive)
        {
            var name = AutomationProperties.GetName(control);
            if (string.IsNullOrEmpty(name))
            {
                if (control is CheckBox cb && cb.Content is string s)
                {
                    name = s;
                }
                else if (control is Button btn && btn.Content is string b)
                {
                    name = b;
                }
            }

            list.Add((control.GetType().Name, name ?? ""));
        }

        return list;
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

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task The_auxiliary_windows_and_dialogs_name_and_focus_every_control_in_both_themes(string theme)
    {
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var problems = new List<string>();

        // MediaPickerWindow
        var track = new MediaTrack("v", MediaRole.Video, "en", "avc1.4d401f", "video/mp4", 2_500_000, 1280, 720, 24, false, null, []);
        var audio = new MediaTrack("a", MediaRole.Audio, "en", "mp4a.40.2", "audio/mp4", 128_000, null, null, 24, false, null, []);
        var presentation = new MediaPresentation("Clip", 24, false, [new MediaVariant(track, audio, null)], [], []);
        var mediaPicker = new MediaPickerViewModel(presentation, "Clip");
        var mediaWindow = new MediaPickerWindow(mediaPicker) { RequestedThemeVariant = variant };
        mediaWindow.Show();
        problems.AddRange(Problems(mediaWindow, $"{theme} media picker window"));
        mediaWindow.Close();

        // SchedulerWindow
        var schedulerService = new RecordingJobService();
        var scheduler = new SchedulerViewModel(schedulerService);
        await scheduler.LoadAsync(1, TestContext.Current.CancellationToken);
        var schedulerWindow = new SchedulerWindow { DataContext = scheduler, RequestedThemeVariant = variant };
        schedulerWindow.Show();
        problems.AddRange(Problems(schedulerWindow, $"{theme} scheduler window"));
        schedulerWindow.Close();

        // LinkPickerWindow
        var batch = new BrowserBatch("https://files.example.org/", [new IpcLink { Url = "https://files.example.org/1.zip", Filename = "1.zip" }], [], "Agent");
        var linkPicker = new LinkPickerViewModel(batch, new RecordingJobService());
        var linkWindow = new LinkPickerWindow { DataContext = linkPicker, RequestedThemeVariant = variant };
        linkWindow.Show();
        problems.AddRange(Problems(linkWindow, $"{theme} link picker window"));
        linkWindow.Close();

        // RefreshLinkWindow
        var refresh = new RefreshLinkViewModel(JobId.New(TimeProvider.System), "file.zip", new FakeRefresher());
        var refreshWindow = new RefreshLinkWindow { DataContext = refresh, RequestedThemeVariant = variant };
        refreshWindow.Show();
        problems.AddRange(Problems(refreshWindow, $"{theme} refresh link window"));
        refreshWindow.Close();

        // ConfirmDeleteWindow
        var confirmDelete = new ConfirmDeleteViewModel("Delete 1 download?", offerFiles: true);
        var confirmDeleteWindow = new ConfirmDeleteWindow { DataContext = confirmDelete, RequestedThemeVariant = variant };
        confirmDeleteWindow.Show();
        problems.AddRange(Problems(confirmDeleteWindow, $"{theme} confirm delete window"));
        confirmDeleteWindow.Close();

        // DuplicatePromptWindow
        var dupPrompt = new DuplicatePromptViewModel(new DuplicateCheckResult(null, true, false, "file (1).zip", [DuplicateAction.Overwrite, DuplicateAction.AutoRename]));
        var duplicateWindow = new DuplicatePromptWindow { DataContext = dupPrompt, RequestedThemeVariant = variant };
        duplicateWindow.Show();
        problems.AddRange(Problems(duplicateWindow, $"{theme} duplicate prompt window"));
        duplicateWindow.Close();

        // TrayFlyoutWindow
        await using var tray = new TrayIconViewModel(new RecordingJobService(), new FakeApp(), action => action());
        var trayWindow = new TrayFlyoutWindow { DataContext = tray, RequestedThemeVariant = variant };
        trayWindow.Show();
        problems.AddRange(Problems(trayWindow, $"{theme} tray flyout window"));
        trayWindow.Close();

        problems.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task Narrator_names_are_present_and_accessible_across_all_windows()
    {
        var job = TestJobs.New("ubuntu.iso", JobState.Downloading, total: 6_000_000_000, done: 3_000_000_000);
        await using var rig = await MainRig.CreateAsync(job);
        var main = new MainWindow { DataContext = rig.Model };
        main.Show();
        var mainInventory = NarratorInventory(main);
        main.Close();

        var row = new JobRowViewModel(job);
        using var details = new DetailsViewModel(row, rig.Service, new ShellSpy(), action => action());
        await details.StartAsync(TestContext.Current.CancellationToken);
        var detailsWindow = new DetailsWindow { DataContext = details };
        detailsWindow.Show();
        var detailsInventory = NarratorInventory(detailsWindow);
        detailsWindow.Close();

        var add = new AddDownloadViewModel(new AddDownloadDraft("https://files.example.org/pack.zip"), new RecordingJobService(), new MemorySites(), _ => Task.FromResult<DuplicateAction?>(null));
        await add.LoadAsync(TestContext.Current.CancellationToken);
        var addWindow = new AddDownloadWindow { DataContext = add };
        addWindow.Show();
        var addInventory = NarratorInventory(addWindow);
        addWindow.Close();

        var track = new MediaTrack("v", MediaRole.Video, "en", "avc1.4d401f", "video/mp4", 2_500_000, 1280, 720, 24, false, null, []);
        var audio = new MediaTrack("a", MediaRole.Audio, "en", "mp4a.40.2", "audio/mp4", 128_000, null, null, 24, false, null, []);
        var presentation = new MediaPresentation("Clip", 24, false, [new MediaVariant(track, audio, null)], [], []);
        var mediaPicker = new MediaPickerViewModel(presentation, "Clip");
        var mediaWindow = new MediaPickerWindow(mediaPicker);
        mediaWindow.Show();
        var mediaInventory = NarratorInventory(mediaWindow);
        mediaWindow.Close();

        var schedulerService = new RecordingJobService();
        var scheduler = new SchedulerViewModel(schedulerService);
        await scheduler.LoadAsync(1, TestContext.Current.CancellationToken);
        var schedulerWindow = new SchedulerWindow { DataContext = scheduler };
        schedulerWindow.Show();
        var schedulerInventory = NarratorInventory(schedulerWindow);
        schedulerWindow.Close();

        mainInventory.Should().NotBeEmpty();
        mainInventory.Should().OnlyContain(item => !string.IsNullOrWhiteSpace(item.Name), "every announced element in MainWindow has a non-empty name");
        detailsInventory.Should().NotBeEmpty();
        detailsInventory.Should().OnlyContain(item => !string.IsNullOrWhiteSpace(item.Name), "every announced element in DetailsWindow has a non-empty name");
        addInventory.Should().NotBeEmpty();
        addInventory.Should().OnlyContain(item => !string.IsNullOrWhiteSpace(item.Name), "every announced element in AddDownloadWindow has a non-empty name");
        mediaInventory.Should().NotBeEmpty();
        mediaInventory.Should().OnlyContain(item => !string.IsNullOrWhiteSpace(item.Name), "every announced element in MediaPickerWindow has a non-empty name");
        schedulerInventory.Should().NotBeEmpty();
        schedulerInventory.Should().OnlyContain(item => !string.IsNullOrWhiteSpace(item.Name), "every announced element in SchedulerWindow has a non-empty name");
    }
}
