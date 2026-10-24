using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Resources;
using Kasi.Media;

namespace Kasi.App.ViewModels;

internal enum MediaPickerFormat
{
    Mp4,
    Matroska,
    AudioOnly,
}

internal sealed record MediaFormatChoice(MediaPickerFormat Format, string Label);

internal sealed record MediaQualityChoice(
    MediaVariant Variant,
    string Label,
    string Details,
    string SizeText,
    string AutomationName,
    bool IsDefault);

internal sealed record MediaSubtitleChoice(string Label, bool IsEnabled);

internal sealed record MediaAudioChoice(MediaTrack Track, string Label);

/// <summary>The picker's immutable answer: the chosen variant (or audio-only track) and container.</summary>
internal sealed record MediaPickerSelection(MediaVariant? Variant, MediaTrack? AudioOnly, MediaPickerFormat Format);

/// <summary>
/// The media picker (KASI-MED-002, TRD 12): every video quality with codec, bitrate and estimated size; the best default at or
/// below the preferred maximum; MP4, MKV or audio-only; subtitle tracks listed but disabled until M3.7. Pure and testable without
/// Avalonia; the window binds to it.
/// </summary>
internal sealed partial class MediaPickerViewModel : ObservableObject
{
    private readonly MediaPresentation _presentation;

    [ObservableProperty]
    private MediaQualityChoice? _selectedQuality;

    [ObservableProperty]
    private MediaFormatChoice? _selectedFormat;

    [ObservableProperty]
    private MediaAudioChoice? _selectedAudio;

    [ObservableProperty]
    private MediaSubtitleChoice? _selectedSubtitle;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    public async Task LoadThumbnailAsync(CancellationToken cancellationToken = default)
    {
        if (!HasThumbnail || Thumbnail is not null)
        {
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await using var stream = await client.GetStreamAsync(ThumbnailUrl!, cancellationToken).ConfigureAwait(false);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            memory.Position = 0;
            Thumbnail = new Bitmap(memory);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ArgumentException)
        {
            // a missing poster is not a reason to block the picker
        }
    }

    public MediaPickerViewModel(
        MediaPresentation presentation,
        string title,
        string? thumbnailUrl = null,
        int preferredMaximumHeight = 1080)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(preferredMaximumHeight);
        _presentation = presentation;
        Title = string.IsNullOrWhiteSpace(title) ? Strings.MediaPickerTitle : title;
        ThumbnailUrl = thumbnailUrl;
        PreferredMaximumHeight = preferredMaximumHeight;
        var defaultVariant = presentation.SelectDefault(preferredMaximumHeight);
        Qualities = presentation.Variants.Select(variant => new MediaQualityChoice(
            variant,
            variant.Label,
            DetailsOf(variant),
            ByteText.Format(variant.EstimatedBytes),
            AutomationOf(variant),
            ReferenceEquals(variant, defaultVariant))).ToArray();
        SelectedQuality = Qualities.FirstOrDefault(choice => choice.IsDefault) ?? FirstOrNull(Qualities);
        Formats = BuildFormats(presentation);
        SelectedFormat = Formats.FirstOrDefault(choice => choice.Format == DefaultFormat(presentation, SelectedQuality?.Variant)) ?? FirstOrNull(Formats);
        AudioTracks = presentation.AudioOnly.Select(track => new MediaAudioChoice(track, AudioLabelOf(track))).ToArray();
        SelectedAudio = FirstOrNull(AudioTracks);
        Subtitles = presentation.Subtitles.Select(track => new MediaSubtitleChoice(SubtitleLabel(track), IsEnabled: false)).ToArray();
        SelectedSubtitle = FirstOrNull(Subtitles);
    }

    public string Title { get; }

    public string? ThumbnailUrl { get; }

    public bool HasThumbnail => !string.IsNullOrWhiteSpace(ThumbnailUrl);

    public int PreferredMaximumHeight { get; }

    public string AutomationName => Strings.MediaPickerAutomationName;

    public string QualityLabel => Strings.MediaPickerQualityLabel;

    public string FormatLabel => Strings.MediaPickerFormatLabel;

    public string SubtitlesLabel => Strings.MediaPickerSubtitlesLabel;

    public string SubtitlesUnavailable => Strings.MediaPickerSubtitlesUnavailable;

    public string AddLabel => Strings.MediaPickerAdd;

    public string CancelLabel => Strings.MediaPickerCancel;

    public string AudioLabel => Strings.MediaPickerAudioLabel;

    public IReadOnlyList<MediaQualityChoice> Qualities { get; }

    public IReadOnlyList<MediaFormatChoice> Formats { get; }

    public IReadOnlyList<MediaAudioChoice> AudioTracks { get; }

    public IReadOnlyList<MediaSubtitleChoice> Subtitles { get; }

    public bool HasQualities => Qualities.Count > 0;

    public bool HasAudioOnly => _presentation.AudioOnly.Count > 0;

    public bool IsAudioOnlySelected => SelectedFormat?.Format == MediaPickerFormat.AudioOnly;

    /// <summary>Set by <see cref="AddCommand"/>; null when the person cancelled.</summary>
    public MediaPickerSelection? Result { get; private set; }

    public event Action? Closed;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (SelectedFormat?.Format == MediaPickerFormat.AudioOnly)
        {
            if (SelectedAudio is null)
            {
                Error = Strings.MediaPickerNoAudio;
                return;
            }

            Result = new MediaPickerSelection(null, SelectedAudio.Track, MediaPickerFormat.AudioOnly);
        }
        else
        {
            if (SelectedQuality is null)
            {
                Error = Strings.MediaPickerNoQuality;
                return;
            }

            Result = new MediaPickerSelection(SelectedQuality.Variant, null, SelectedFormat?.Format ?? MediaPickerFormat.Mp4);
        }

        Closed?.Invoke();
    }

    private bool CanAdd() =>
        SelectedFormat is not null
        && (SelectedFormat.Format == MediaPickerFormat.AudioOnly
            ? SelectedAudio is not null
            : SelectedQuality is not null);

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Closed?.Invoke();
    }

    partial void OnSelectedFormatChanged(MediaFormatChoice? value)
    {
        AddCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsAudioOnlySelected));
    }

    partial void OnSelectedAudioChanged(MediaAudioChoice? value) => AddCommand.NotifyCanExecuteChanged();

    partial void OnSelectedQualityChanged(MediaQualityChoice? value) => AddCommand.NotifyCanExecuteChanged();

    private static T? FirstOrNull<T>(IReadOnlyList<T> items) where T : class => items.Count > 0 ? items[0] : null;

    private static readonly string[] Mp4VideoCodecs = ["avc", "h264", "hev", "hvc", "av01"];

    private static readonly string[] Mp4AudioCodecs = ["mp4a", "aac", "ac-3", "ec-3", "ac3", "eac3"];

    private static IReadOnlyList<MediaFormatChoice> BuildFormats(MediaPresentation presentation) =>
    [
        new(MediaPickerFormat.Mp4, Strings.MediaPickerMp4),
        new(MediaPickerFormat.Matroska, Strings.MediaPickerMkv),
        .. presentation.AudioOnly.Count > 0
            ? [new MediaFormatChoice(MediaPickerFormat.AudioOnly, Strings.MediaPickerAudioOnly)]
            : Array.Empty<MediaFormatChoice>(),
    ];

    private static MediaPickerFormat DefaultFormat(MediaPresentation presentation, MediaVariant? variant)
    {
        variant ??= presentation.SelectDefault();
        return variant is not null && IsMp4Compatible(variant) ? MediaPickerFormat.Mp4 : MediaPickerFormat.Matroska;
    }

    private static bool IsMp4Compatible(MediaVariant variant)
    {
        var video = variant.Video.Codecs ?? "";
        var audio = variant.Audio?.Codecs ?? "";
        var videoOk = Mp4VideoCodecs
            .Any(codec => video.Contains(codec, StringComparison.OrdinalIgnoreCase));
        var audioOk = string.IsNullOrEmpty(audio)
            || Mp4AudioCodecs
                .Any(codec => audio.Contains(codec, StringComparison.OrdinalIgnoreCase));
        return videoOk && audioOk;
    }

    private static string DetailsOf(MediaVariant variant)
    {
        var codec = string.IsNullOrWhiteSpace(variant.Video.Codecs) ? Strings.MediaPickerUnknownCodec : variant.Video.Codecs;
        var bitrate = variant.Video.Bandwidth is { } bandwidth
            ? string.Create(CultureInfo.CurrentCulture, $"{bandwidth / 1_000_000.0:0.#} Mbps")
            : Strings.MediaPickerUnknownBitrate;
        return string.Create(CultureInfo.CurrentCulture, $"{codec} · {bitrate}");
    }

    private static string AutomationOf(MediaVariant variant) =>
        string.Create(CultureInfo.CurrentCulture, $"{variant.Label}, {DetailsOf(variant)}, {ByteText.Format(variant.EstimatedBytes)}");

    private static string AudioLabelOf(MediaTrack track) =>
        string.IsNullOrWhiteSpace(track.Language) ? Strings.MediaPickerAudioLabel : $"{Strings.MediaPickerAudioLabel} ({track.Language})";

    private static string SubtitleLabel(MediaTrack track) =>
        string.IsNullOrWhiteSpace(track.Language) ? Strings.MediaPickerSubtitle : $"{Strings.MediaPickerSubtitle} ({track.Language})";

    // Kept for the window's optional thumbnail area; the URL is never logged.
    public string ThumbnailText => HasThumbnail ? Strings.MediaPickerThumbnail : string.Empty;
}
