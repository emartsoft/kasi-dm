using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kasi.App.Resources;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Media;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-MED-002")]
[Trait("Requirement", "KASI-UI-010")]
public sealed class MediaPickerWindowTests
{
    [AvaloniaFact]
    public void The_picker_exposes_named_quality_format_and_action_controls()
    {
        var track = new MediaTrack("v", MediaRole.Video, "en", "avc1.4d401f", "video/mp4", 2_500_000, 1280, 720, 24, false, null, []);
        var audio = new MediaTrack("a", MediaRole.Audio, "en", "mp4a.40.2", "audio/mp4", 128_000, null, null, 24, false, null, []);
        var model = new MediaPickerViewModel(
            new MediaPresentation("Clip", 24, false, [new MediaVariant(track, audio, null)], [], []),
            "Clip");
        var window = new MediaPickerWindow(model);
        window.Show();

        window.IsVisible.Should().BeTrue();
        AutomationProperties.GetName(window).Should().Be(Strings.MediaPickerAutomationName);
        var list = window.GetVisualDescendants().OfType<ListBox>()
            .Single(box => AutomationProperties.GetName(box) == Strings.MediaPickerQualityLabel);
        AutomationProperties.GetName(list).Should().Be(Strings.MediaPickerQualityLabel);
        var buttons = window.GetVisualDescendants().OfType<Button>().Select(button => AutomationProperties.GetName(button)).ToList();
        buttons.Should().Contain([Strings.MediaPickerAdd, Strings.MediaPickerCancel]);
        window.GetVisualDescendants().OfType<ListBox>()
            .Single(list => AutomationProperties.GetName(list) == Strings.MediaPickerSubtitlesLabel)
            .IsEnabled.Should().BeTrue();
        window.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).Should().Contain("720p");

        window.Close();
    }
}
