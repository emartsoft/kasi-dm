using Kasi.App.Resources;
using Kasi.App.ViewModels;
using Kasi.Media;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-MED-002")]
[Trait("TC", "TC-MED-002")]
public sealed class MediaPickerViewModelTests
{
    [Fact]
    public void The_default_is_the_highest_quality_at_or_below_the_preferred_maximum()
    {
        var model = new MediaPickerViewModel(Presentation(), "Clip", preferredMaximumHeight: 720);

        model.SelectedQuality!.Variant.Height.Should().Be(720);
        model.SelectedQuality.IsDefault.Should().BeTrue();
        model.SelectedFormat!.Format.Should().Be(MediaPickerFormat.Mp4);
        model.Qualities.Should().HaveCount(3);
        model.Qualities.Select(quality => quality.Label).Should().Equal("1080p", "720p", "360p");
        model.Qualities.Should().OnlyContain(quality => !string.IsNullOrWhiteSpace(quality.SizeText));
    }

    [Fact]
    public void A_codec_that_does_not_fit_mp4_defaults_to_mkv_and_can_be_switched_back()
    {
        var model = new MediaPickerViewModel(Presentation(), "Clip");

        model.SelectedQuality!.Variant.Height.Should().Be(1080);
        model.SelectedFormat!.Format.Should().Be(MediaPickerFormat.Matroska);

        model.SelectedFormat = model.Formats.Single(format => format.Format == MediaPickerFormat.Mp4);
        model.SelectedFormat.Format.Should().Be(MediaPickerFormat.Mp4);
    }

    [Fact]
    public void Subtitles_are_listed_but_disabled_and_the_result_carries_the_choice()
    {
        var model = new MediaPickerViewModel(Presentation(), "Clip", "https://example.com/poster.jpg", preferredMaximumHeight: 720);
        MediaPickerSelection? selected = null;
        model.Closed += () => selected = model.Result;

        model.Subtitles.Should().ContainSingle().Which.IsEnabled.Should().BeFalse();
        model.Qualities[1].AutomationName.Should().Contain("720p");
        model.HasThumbnail.Should().BeTrue();

        model.AddCommand.Execute(null);

        selected.Should().NotBeNull();
        selected!.Variant!.Height.Should().Be(720);
        selected.Format.Should().Be(MediaPickerFormat.Mp4);
    }

    [Fact]
    public void Cancel_returns_no_selection()
    {
        var model = new MediaPickerViewModel(Presentation(), "Clip");
        MediaPickerSelection? selected = new(null, null, MediaPickerFormat.Mp4);
        model.Closed += () => selected = model.Result;

        model.CancelCommand.Execute(null);

        selected.Should().BeNull();
        model.Title.Should().Be("Clip");
        model.AutomationName.Should().Be(Strings.MediaPickerAutomationName);
    }

    [Fact]
    public void Audio_only_is_offered_only_when_the_presentation_has_an_audio_track()
    {
        var withAudio = new MediaPickerViewModel(Presentation(audioOnly: true), "Clip");
        withAudio.Formats.Select(format => format.Format).Should().Contain(MediaPickerFormat.AudioOnly);
        withAudio.AudioTracks.Should().ContainSingle();
        withAudio.SelectedFormat = withAudio.Formats.Single(format => format.Format == MediaPickerFormat.AudioOnly);
        withAudio.AddCommand.Execute(null);
        withAudio.Result!.AudioOnly.Should().NotBeNull();

        var withoutAudio = new MediaPickerViewModel(Presentation(), "Clip");
        withoutAudio.Formats.Select(format => format.Format).Should().NotContain(MediaPickerFormat.AudioOnly);
    }

    private static MediaPresentation Presentation(bool audioOnly = false)
    {
        var video360 = Track("v360", MediaRole.Video, "avc1.4d401e", 800_000, 640, 360);
        var video720 = Track("v720", MediaRole.Video, "avc1.4d401f", 2_500_000, 1280, 720);
        var video1080 = Track("v1080", MediaRole.Video, "vp09.00.10.08", 4_000_000, 1920, 1080);
        var audio = Track("a1", MediaRole.Audio, "mp4a.40.2", 128_000, null, null);
        var subtitle = Track("s1", MediaRole.Subtitle, null, null, null, null) with { Language = "en" };
        return new MediaPresentation(
            "Clip",
            24,
            false,
            [
                new MediaVariant(video1080, audio, subtitle),
                new MediaVariant(video720, audio, subtitle),
                new MediaVariant(video360, audio, subtitle),
            ],
            audioOnly ? [audio] : [],
            [subtitle]);
    }

    private static MediaTrack Track(string id, MediaRole role, string? codecs, int? bandwidth, int? width, int? height) =>
        new(id, role, "en", codecs, role == MediaRole.Audio ? "audio/mp4" : "video/mp4", bandwidth, width, height, 24, false, null, []);
}
