using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Browser;
using Kasi.App.Controls;
using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Resources;
using Kasi.Core;
using Kasi.Ipc.Messages;

namespace Kasi.App.ViewModels;

internal sealed record BypassKeyChoice(BypassKey Key, string Label);

internal sealed record ThemeChoice(ThemePreference Preference, string Label);

/// <summary>A page of the navigation: <see cref="Id"/> is stable (0 General, 1 Browser, 2 File types and paths, 3 Scheduler, 4 About).</summary>
internal sealed record SettingsPage(int Id, string Label, string Glyph, string Description);

/// <summary>A result of "Find a setting": the label that matched and the page it lives on.</summary>
internal sealed record SettingHit(string Label, int PageId);

/// <summary>What the window needs from the app beyond the stores: the facts of the "This PC" card and the two things it can open.</summary>
internal sealed record SettingsEnvironment(string DataFolder, string Version, Func<int> ConnectedBrowsers, Action OpenScheduler, IShell Shell);

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
    private readonly SettingsEnvironment? _environment;
    private readonly IJobService? _jobs;
    private bool _syncingPage;

    [ObservableProperty]
    private ObservableCollection<SpeedProfile> _speedProfiles = [];

    [ObservableProperty]
    private SpeedProfile? _selectedSpeedProfile;

    [ObservableProperty]
    private string _speedProfileNameText = string.Empty;

    [ObservableProperty]
    private decimal _speedProfileMegabytes;

    [ObservableProperty]
    private string _speedProfileStatus = string.Empty;

    [ObservableProperty]
    private ThemeChoice _selectedTheme;

    [ObservableProperty]
    private bool _scanDownloads;

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
    [NotifyPropertyChangedFor(nameof(IsGeneralPage), nameof(IsBrowserPage), nameof(IsFilesPage), nameof(IsSchedulerPage), nameof(IsAboutPage),
        nameof(PageTitle), nameof(PageDescription), nameof(Breadcrumb))]
    private int _selectedTab;

    [ObservableProperty]
    private SettingsPage? _selectedPage;

    [ObservableProperty]
    private string _searchText = string.Empty;

    public SettingsViewModel(IBrowserSettings store, IUiSettings? ui = null, CategoriesViewModel? categories = null, SettingsEnvironment? environment = null, IJobService? jobs = null)
    {
        _store = store;
        _ui = ui;
        _environment = environment;
        _jobs = jobs;
        Categories = categories;
        var pages = new List<SettingsPage>
        {
            new(0, Strings.SettingsPageGeneral, Icons.Settings, Strings.SettingsPageGeneralDescription),
            new(1, Strings.SettingsPageBrowser, Icons.Globe, Strings.SettingsPageBrowserDescription),
        };
        if (categories is not null)
        {
            pages.Add(new SettingsPage(2, Strings.SettingsPageFiles, Icons.Folder, Strings.SettingsPageFilesDescription));
        }

        pages.Add(new SettingsPage(3, Strings.SettingsPageScheduler, Icons.Clock, Strings.SettingsPageSchedulerDescription));
        pages.Add(new SettingsPage(4, Strings.SettingsPageAbout, Icons.Info, Strings.SettingsPageAboutDescription));
        Pages = pages;
        _selectedPage = pages[0];
        Hits = [];
        Themes =
        [
            new(ThemePreference.System, Strings.ThemeSystem),
            new(ThemePreference.Light, Strings.ThemeLight),
            new(ThemePreference.Dark, Strings.ThemeDark),
        ];
        _selectedTheme = Themes.First(t => t.Preference == (ui?.Current.Theme ?? ThemePreference.System));
        _scanDownloads = ui?.Current.ScanDownloads ?? false;
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

    public IReadOnlyList<SettingsPage> Pages { get; }

    /// <summary>The settings whose label contains what was typed in "Find a setting".</summary>
    public ObservableCollection<SettingHit> Hits { get; }

    public bool HasHits => Hits.Count > 0;

    public bool IsGeneralPage => SelectedTab == 0;

    public bool IsBrowserPage => SelectedTab == 1;

    public bool IsFilesPage => SelectedTab == 2;

    public bool IsSchedulerPage => SelectedTab == 3;

    public bool IsAboutPage => SelectedTab == 4;

    public string PageTitle => CurrentPage.Label;

    public string PageDescription => CurrentPage.Description;

    public string Breadcrumb => Strings.SettingsBreadcrumbRoot + " / " + CurrentPage.Label;

    private SettingsPage CurrentPage => Pages.FirstOrDefault(p => p.Id == SelectedTab) ?? Pages[0];

    // the "This PC" card: real facts, no profiles (Kasi has none)
    public string ThisPcLabel => Strings.SettingsThisPc;

    public string DataFolderLabel => Strings.SettingsDataFolder;

    public string DataFolderText => _environment is null ? string.Empty : ShortFolder(_environment.DataFolder);

    public string VersionLabel => Strings.SettingsVersion;

    public string VersionText => _environment?.Version ?? string.Empty;

    public string ExtensionText => _environment is { } env && env.ConnectedBrowsers() > 0 ? Strings.SidebarExtensionConnected : Strings.SidebarExtensionNone;

    public string OpenDataFolderLabel => Strings.SettingsOpenDataFolder;

    public string OpenSchedulerLabel => Strings.SettingsOpenScheduler;

    public string FindLabel => Strings.SettingsFind;

    public string FindName => Strings.SettingsFindName;

    public string NavigationName => Strings.SettingsNavigation;

    public string RestoreDefaultsLabel => Strings.SettingsRestoreDefaults;

    public string ResetLabel => Strings.SettingsReset;

    public string ResetExtensionsName => Strings.SettingsReset + " " + ExtensionsLabel;

    public string ResetMimeTypesName => Strings.SettingsReset + " " + MimeTypesLabel;

    public string ResetExcludedSitesName => Strings.SettingsReset + " " + ExcludedSitesLabel;

    public string RulesHeading => Strings.SettingsRulesHeading;

    public string RulesNote => Strings.SettingsRulesNote;

    public string FooterNote => Strings.SettingsRestartNote;

    public string AboutTagline => Strings.AboutTagline;

    public string AboutOwner => Strings.AboutOwner;

    public string AboutNoticesLabel => Strings.AboutNotices;

    public string SpeedProfilesLabel => Strings.SettingsSpeedProfiles;

    public string SpeedProfileNameLabel => Strings.SettingsSpeedProfileName;

    public string SpeedProfileLimitLabel => Strings.SettingsSpeedProfileLimit;

    public string SpeedProfileAddLabel => Strings.SettingsSpeedProfileAdd;

    public string SpeedProfileSaveLabel => Strings.SettingsSpeedProfileSave;

    public string SpeedProfileDeleteLabel => Strings.SettingsSpeedProfileDelete;

    public string SpeedProfileEmptyLabel => Strings.SettingsSpeedProfileEmpty;

    public bool HasSpeedProfiles => SpeedProfiles.Count > 0;

    public IReadOnlyList<string> Notices { get; } =
    [
        "Avalonia, FluentAvalonia, CommunityToolkit.Mvvm - MIT",
        "Microsoft.Data.Sqlite, SQLite - MIT, public domain",
        "Dapper, Serilog - Apache-2.0",
        "FluentFTP, MonoTorrent, Velopack - MIT",
        "FFmpeg (LGPL build, run as a separate program) - LGPL-2.1",
    ];

    /// <summary>"...\Kasi" instead of the whole path of a user folder.</summary>
    internal static string ShortFolder(string path)
    {
        var separators = new[] { '\\', '/' };
        var trimmed = path.TrimEnd(separators);
        var parts = trimmed.Split(separators, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 2 ? trimmed : "..." + Path.DirectorySeparatorChar + string.Join(Path.DirectorySeparatorChar, parts[^2..]);
    }

    public string CategoriesTab => Strings.CategoriesTab;

    public IReadOnlyList<BypassKeyChoice> BypassKeys { get; }

    public IReadOnlyList<ThemeChoice> Themes { get; }

    public string ThemeLabel => Strings.ThemeLabel;

    public string ScanDownloadsLabel => Strings.SettingsScanDownloads;

    public string ScanDownloadsDescription => Strings.SettingsScanDownloadsDescription;

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

    // the navigation list and the page number are one thing
    partial void OnSelectedTabChanged(int value)
    {
        if (_syncingPage)
        {
            return;
        }

        _syncingPage = true;
        SelectedPage = Pages.FirstOrDefault(p => p.Id == value) ?? SelectedPage;
        _syncingPage = false;
    }

    partial void OnSelectedPageChanged(SettingsPage? value)
    {
        if (_syncingPage || value is null)
        {
            return;
        }

        _syncingPage = true;
        SelectedTab = value.Id;
        _syncingPage = false;
    }

    partial void OnSearchTextChanged(string value)
    {
        Hits.Clear();
        var query = value.Trim();
        if (query.Length > 0)
        {
            foreach (var hit in Searchable().Where(h => h.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(8))
            {
                Hits.Add(hit);
            }
        }

        OnPropertyChanged(nameof(HasHits));
    }

    // every label a person can look for, with the page it is on
    private IEnumerable<SettingHit> Searchable()
    {
        foreach (var page in Pages)
        {
            yield return new SettingHit(page.Label, page.Id);
        }

        yield return new SettingHit(ThemeLabel, 0);
        yield return new SettingHit(ScanDownloadsLabel, 0);
        foreach (var label in new[] { EnabledLabel, ShowAddDialogLabel, ClipboardCaptureLabel, ExtensionsLabel, MimeTypesLabel, ExcludedSitesLabel, BypassKeyLabel, MinSizeLabel })
        {
            yield return new SettingHit(label, 1);
        }
    }

    [RelayCommand]
    private void OpenHit(SettingHit? hit)
    {
        if (hit is not null)
        {
            SelectedTab = hit.PageId;
            SearchText = string.Empty;
        }
    }

    [RelayCommand]
    private void ResetExtensions() => ExtensionsText = string.Join(Environment.NewLine, BrowserSettings.DefaultExtensions);

    [RelayCommand]
    private void ResetMimeTypes() => MimeTypesText = string.Join(Environment.NewLine, BrowserSettings.DefaultMimeTypes);

    [RelayCommand]
    private void ResetExcludedSites() => ExcludedSitesText = string.Empty;

    /// <summary>Puts every setting of the form back to its default; nothing is saved until Save.</summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        Load(new BrowserSettings { AckTimeoutMs = _store.Current.AckTimeoutMs });
        SelectedTheme = Themes.First(t => t.Preference == ThemePreference.System);
        ScanDownloads = false;
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (_environment is { } env)
        {
            env.Shell.OpenFolder(env.DataFolder);
        }
    }

    [RelayCommand]
    private void OpenScheduler() => _environment?.OpenScheduler();

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

    public async Task LoadSpeedProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (_jobs is null)
        {
            return;
        }

        var list = await _jobs.SpeedProfilesAsync(cancellationToken).ConfigureAwait(true);
        SpeedProfiles = new ObservableCollection<SpeedProfile>(list);
        OnPropertyChanged(nameof(HasSpeedProfiles));
        SelectedSpeedProfile = list.Count > 0 ? list[0] : null;
        if (list.Count == 0)
        {
            SpeedProfileNameText = string.Empty;
            SpeedProfileMegabytes = 0;
        }
    }

    partial void OnSelectedSpeedProfileChanged(SpeedProfile? value)
    {
        SpeedProfileNameText = value?.Name ?? string.Empty;
        SpeedProfileMegabytes = value?.LimitBps is { } limit ? (decimal)limit / MegabyteBytes : 0;
    }

    [RelayCommand]
    private async Task AddSpeedProfileAsync()
    {
        if (_jobs is null || !TryReadProfile(out var profile))
        {
            return;
        }

        await _jobs.SaveSpeedProfileAsync(profile with { SortOrder = SpeedProfiles.Count }).ConfigureAwait(true);
        await LoadSpeedProfilesAsync().ConfigureAwait(true);
        SpeedProfileStatus = string.Empty;
    }

    [RelayCommand]
    private async Task SaveSpeedProfileAsync()
    {
        if (_jobs is null || SelectedSpeedProfile is null || !TryReadProfile(out var profile))
        {
            return;
        }

        await _jobs.SaveSpeedProfileAsync(profile with { Id = SelectedSpeedProfile.Id, SortOrder = SelectedSpeedProfile.SortOrder }).ConfigureAwait(true);
        await LoadSpeedProfilesAsync().ConfigureAwait(true);
        SpeedProfileStatus = string.Empty;
    }

    [RelayCommand]
    private async Task DeleteSpeedProfileAsync()
    {
        if (_jobs is null || SelectedSpeedProfile is null)
        {
            return;
        }

        await _jobs.DeleteSpeedProfileAsync(SelectedSpeedProfile.Id).ConfigureAwait(true);
        await LoadSpeedProfilesAsync().ConfigureAwait(true);
    }

    private bool TryReadProfile(out SpeedProfile profile)
    {
        var name = SpeedProfileNameText.Trim();
        if (name.Length == 0)
        {
            SpeedProfileStatus = Strings.SettingsSpeedProfileName;
            profile = null!;
            return false;
        }

        profile = new SpeedProfile
        {
            Id = SelectedSpeedProfile?.Id ?? 0,
            Name = name,
            LimitBps = SpeedProfileMegabytes <= 0 ? null : checked((long)(SpeedProfileMegabytes * MegabyteBytes)),
            SortOrder = SelectedSpeedProfile?.SortOrder ?? SpeedProfiles.Count,
        };
        return true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await _store.SaveAsync(ToSettings(), CancellationToken.None).ConfigureAwait(true);
            if (_ui is not null && (_ui.Current.Theme != SelectedTheme.Preference || _ui.Current.ScanDownloads != ScanDownloads))
            {
                await _ui.SaveAsync(_ui.Current with { Theme = SelectedTheme.Preference, ScanDownloads = ScanDownloads }).ConfigureAwait(true);
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
