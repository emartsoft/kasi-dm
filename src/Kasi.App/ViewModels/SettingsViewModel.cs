using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.Ipc.Messages;

namespace Kasi.App.ViewModels;

internal sealed record BypassKeyChoice(BypassKey Key, string Label);

internal sealed record ThemeChoice(ThemePreference Preference, string Label);

/// <summary>
/// The Settings window's Browser section (the skeleton of M2.8b): the rules Kasi pushes to the extension. Text lists hold one entry per
/// line (commas and semicolons also separate), cleaned by <see cref="BrowserSettings.Normalized"/> on save, which is also what the pipe sends.
/// </summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private const long MegabyteBytes = 1024 * 1024;

    private static readonly char[] _separators = ['\r', '\n', ',', ';'];

    private readonly IBrowserSettings _store;
    private readonly IUiSettings? _ui;

    [ObservableProperty]
    private ThemeChoice _selectedTheme;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _showAddDialog;

    [ObservableProperty]
    private bool _clipboardCapture;

    [ObservableProperty]
    private string _extensionsText = string.Empty;

    [ObservableProperty]
    private string _mimeTypesText = string.Empty;

    [ObservableProperty]
    private string _excludedSitesText = string.Empty;

    [ObservableProperty]
    private BypassKeyChoice _selectedBypassKey;

    [ObservableProperty]
    private decimal _minSizeMegabytes;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private int _selectedTab;

    public SettingsViewModel(IBrowserSettings store, IUiSettings? ui = null, CategoriesViewModel? categories = null)
    {
        _store = store;
        _ui = ui;
        Categories = categories;
        Themes =
        [
            new(ThemePreference.System, Strings.ThemeSystem),
            new(ThemePreference.Light, Strings.ThemeLight),
            new(ThemePreference.Dark, Strings.ThemeDark),
        ];
        _selectedTheme = Themes.First(t => t.Preference == (ui?.Current.Theme ?? ThemePreference.System));
        BypassKeys =
        [
            new(BypassKey.Alt, Strings.BypassKeyAlt),
            new(BypassKey.Ctrl, Strings.BypassKeyCtrl),
            new(BypassKey.Shift, Strings.BypassKeyShift),
            new(BypassKey.None, Strings.BypassKeyNone),
        ];
        _selectedBypassKey = BypassKeys[0];
        Load(store.Current);
    }

    /// <summary>The Categories page, which saves each change at once; null when the window is opened without the category stores.</summary>
    public CategoriesViewModel? Categories { get; }

    public bool HasCategories => Categories is not null;

    public string CategoriesTab => Strings.CategoriesTab;

    public IReadOnlyList<BypassKeyChoice> BypassKeys { get; }

    public IReadOnlyList<ThemeChoice> Themes { get; }

    public string ThemeLabel => Strings.ThemeLabel;

    public string Title => Strings.SettingsTitle;

    public string AutomationName => Strings.SettingsAutomationName;

    /// <summary>Raised after a successful save, so the window can close.</summary>
    public event Action? Saved;

    public void Load(BrowserSettings settings)
    {
        Enabled = settings.Enabled;
        ShowAddDialog = settings.ShowAddDialog;
        ClipboardCapture = settings.ClipboardCapture;
        ExtensionsText = string.Join(Environment.NewLine, settings.Extensions);
        MimeTypesText = string.Join(Environment.NewLine, settings.MimeTypes);
        ExcludedSitesText = string.Join(Environment.NewLine, settings.ExcludedSites);
        SelectedBypassKey = BypassKeys.First(choice => choice.Key == settings.BypassKey);
        MinSizeMegabytes = settings.MinSizeBytes is { } bytes ? (decimal)bytes / MegabyteBytes : 0;
    }

    public void ShowSection(SettingsSection section) => SelectedTab = section == SettingsSection.General ? 0 : 1;

    /// <summary>What the form holds, as settings (not yet cleaned; <see cref="BrowserSettings.Normalized"/> does that on save).</summary>
    public BrowserSettings ToSettings() => new()
    {
        Enabled = Enabled,
        ShowAddDialog = ShowAddDialog,
        ClipboardCapture = ClipboardCapture,
        Extensions = Split(ExtensionsText),
        MimeTypes = Split(MimeTypesText),
        ExcludedSites = Split(ExcludedSitesText),
        BypassKey = SelectedBypassKey.Key,
        AckTimeoutMs = _store.Current.AckTimeoutMs,
        MinSizeBytes = MinSizeMegabytes > 0 ? (long)Math.Min(MinSizeMegabytes * MegabyteBytes, long.MaxValue) : null,
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(ToSettings(), CancellationToken.None).ConfigureAwait(true);
            if (_ui is not null && _ui.Current.Theme != SelectedTheme.Preference)
            {
                await _ui.SaveAsync(_ui.Current with { Theme = SelectedTheme.Preference }).ConfigureAwait(true);
            }

            StatusText = string.Empty;
            Saved?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = Strings.SettingsSaveFailed;
        }
    }

    private static List<string> Split(string text) =>
        [.. text.Split(_separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // the two text labels the window binds to, kept here so the view holds no strings
    public string GeneralTab => Strings.SettingsGeneralTab;

    public string BrowserTab => Strings.SettingsBrowserTab;

    public string GeneralPlaceholder => Strings.SettingsGeneralPlaceholder;

    public string EnabledLabel => Strings.BrowserInterceptionEnabled;

    public string ClipboardCaptureLabel => Strings.BrowserClipboardCapture;

    public string ShowAddDialogLabel => Strings.BrowserShowAddDialog;

    public string ExtensionsLabel => Strings.BrowserExtensionsLabel;

    public string MimeTypesLabel => Strings.BrowserMimeTypesLabel;

    public string ExcludedSitesLabel => Strings.BrowserExcludedSitesLabel;

    public string BypassKeyLabel => Strings.BrowserBypassKeyLabel;

    public string MinSizeLabel => Strings.BrowserMinSizeLabel;

    public string SaveLabel => Strings.SettingsSaveButton;

    public string CancelLabel => Strings.SettingsCancelButton;
}
