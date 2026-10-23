using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kasi.App.Browser;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Engine;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-UI-010")]
public class AddWindowTests
{
    [AvaloniaFact]
    public async Task The_add_window_shows_every_field_with_an_automation_name()
    {
        var vm = new AddDownloadViewModel(new AddDownloadDraft("https://files.example.org/pack.zip"), new RecordingJobService(), new MemorySites(), _ => Task.FromResult<DuplicateAction?>(null));
        await vm.LoadAsync(TestContext.Current.CancellationToken);
        var window = new AddDownloadWindow { DataContext = vm };

        window.Show();

        AutomationProperties.GetName(window).Should().Be("Add download window");
        var controls = window.GetVisualDescendants().OfType<Control>().Where(c => (c is TextBox or ComboBox or CheckBox or NumericUpDown or Button) && c.FindAncestorOfType<NumericUpDown>() is null && c.FindAncestorOfType<ComboBox>() is null).ToList(); // the parts inside a spinner or a drop-down belong to that control
        controls.Should().HaveCountGreaterThanOrEqualTo(11);
        controls.Should().OnlyContain(c => !string.IsNullOrEmpty(AutomationProperties.GetName(c)), "every input has a name for screen readers");
        window.GetVisualDescendants().OfType<TextBox>().First().Text.Should().Be("https://files.example.org/pack.zip");
    }

    [AvaloniaFact]
    public void The_duplicate_window_has_a_button_per_offered_action_and_a_cancel()
    {
        var check = new DuplicateCheckResult(null, true, false, "pack (1).zip", [DuplicateAction.Overwrite, DuplicateAction.AutoRename]);
        var window = new DuplicatePromptWindow { DataContext = new DuplicatePromptViewModel(check) };

        window.Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().ToList();
        buttons.Should().HaveCount(3);
        buttons.Should().OnlyContain(b => !string.IsNullOrEmpty(AutomationProperties.GetName(b)));
    }

    [Fact]
    public void The_settings_form_round_trips_the_add_dialog_switch()
    {
        var store = new TestSettings(new BrowserSettings { ShowAddDialog = false });
        var form = new SettingsViewModel(store);

        form.ShowAddDialog.Should().BeFalse();
        form.ShowAddDialog = true;

        form.ToSettings().ShowAddDialog.Should().BeTrue();
    }

    private sealed class TestSettings(BrowserSettings current) : IBrowserSettings
    {
        public BrowserSettings Current { get; } = current;

#pragma warning disable CS0067
        public event Action<BrowserSettings>? Changed;
#pragma warning restore CS0067

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
