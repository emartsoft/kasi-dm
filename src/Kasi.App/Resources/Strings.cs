using System.Globalization;
using System.Resources;

namespace Kasi.App.Resources;

/// <summary>Typed access to <c>Strings.resx</c>; all user-visible text lives there (CONTRIBUTING 6, localisation-ready).</summary>
internal static class Strings
{
    private static readonly ResourceManager _resources = new("Kasi.App.Resources.Strings", typeof(Strings).Assembly);

    public static string AppTitle => Get(nameof(AppTitle));

    public static string MainWindowAutomationName => Get(nameof(MainWindowAutomationName));

    public static string JobListAutomationName => Get(nameof(JobListAutomationName));

    public static string JobColumnName => Get(nameof(JobColumnName));

    public static string JobColumnState => Get(nameof(JobColumnState));

    public static string JobColumnProgress => Get(nameof(JobColumnProgress));

    public static string JobColumnSize => Get(nameof(JobColumnSize));

    public static string AddTitle => Get(nameof(AddTitle));

    public static string AddAutomationName => Get(nameof(AddAutomationName));

    public static string AddUrlLabel => Get(nameof(AddUrlLabel));

    public static string AddFileNameLabel => Get(nameof(AddFileNameLabel));

    public static string AddFolderLabel => Get(nameof(AddFolderLabel));

    public static string AddCategoryLabel => Get(nameof(AddCategoryLabel));

    public static string AddQueueLabel => Get(nameof(AddQueueLabel));

    public static string AddConnectionsLabel => Get(nameof(AddConnectionsLabel));

    public static string AddStartNowLabel => Get(nameof(AddStartNowLabel));

    public static string AddChecksumLabel => Get(nameof(AddChecksumLabel));

    public static string AddChecksumHexLabel => Get(nameof(AddChecksumHexLabel));

    public static string AddChecksumNone => Get(nameof(AddChecksumNone));

    public static string AddRememberLabel => Get(nameof(AddRememberLabel));

    public static string AddAddButton => Get(nameof(AddAddButton));

    public static string AddCancelButton => Get(nameof(AddCancelButton));

    public static string AddErrorUrl => Get(nameof(AddErrorUrl));

    public static string AddErrorFileName => Get(nameof(AddErrorFileName));

    public static string AddErrorQueue => Get(nameof(AddErrorQueue));

    public static string AddErrorConnections => Get(nameof(AddErrorConnections));

    public static string AddErrorChecksum => Get(nameof(AddErrorChecksum));

    public static string AddErrorFailed => Get(nameof(AddErrorFailed));

    public static string DuplicateTitle => Get(nameof(DuplicateTitle));

    public static string DuplicateMessage => Get(nameof(DuplicateMessage));

    public static string DuplicateResume => Get(nameof(DuplicateResume));

    public static string DuplicateOverwrite => Get(nameof(DuplicateOverwrite));

    public static string DuplicateRename => Get(nameof(DuplicateRename));

    public static string BrowserShowAddDialog => Get(nameof(BrowserShowAddDialog));

    public static string PickerTitle => Get(nameof(PickerTitle));

    public static string PickerAutomationName => Get(nameof(PickerAutomationName));

    public static string PickerFilterLabel => Get(nameof(PickerFilterLabel));

    public static string PickerExtensionLabel => Get(nameof(PickerExtensionLabel));

    public static string PickerAllExtensions => Get(nameof(PickerAllExtensions));

    public static string PickerSelectAll => Get(nameof(PickerSelectAll));

    public static string PickerSelectNone => Get(nameof(PickerSelectNone));

    public static string PickerSummary => Get(nameof(PickerSummary));

    public static string PickerAdd => Get(nameof(PickerAdd));

    public static string PickerFailed => Get(nameof(PickerFailed));

    public static string SettingsTitle => Get(nameof(SettingsTitle));

    public static string SettingsAutomationName => Get(nameof(SettingsAutomationName));

    public static string SettingsGeneralTab => Get(nameof(SettingsGeneralTab));

    public static string SettingsBrowserTab => Get(nameof(SettingsBrowserTab));

    public static string SettingsGeneralPlaceholder => Get(nameof(SettingsGeneralPlaceholder));

    public static string BrowserInterceptionEnabled => Get(nameof(BrowserInterceptionEnabled));

    public static string BrowserExtensionsLabel => Get(nameof(BrowserExtensionsLabel));

    public static string BrowserMimeTypesLabel => Get(nameof(BrowserMimeTypesLabel));

    public static string BrowserExcludedSitesLabel => Get(nameof(BrowserExcludedSitesLabel));

    public static string BrowserBypassKeyLabel => Get(nameof(BrowserBypassKeyLabel));

    public static string BrowserMinSizeLabel => Get(nameof(BrowserMinSizeLabel));

    public static string BypassKeyAlt => Get(nameof(BypassKeyAlt));

    public static string BypassKeyCtrl => Get(nameof(BypassKeyCtrl));

    public static string BypassKeyShift => Get(nameof(BypassKeyShift));

    public static string BypassKeyNone => Get(nameof(BypassKeyNone));

    public static string SettingsSaveButton => Get(nameof(SettingsSaveButton));

    public static string SettingsCancelButton => Get(nameof(SettingsCancelButton));

    public static string SettingsSaveFailed => Get(nameof(SettingsSaveFailed));

    private static string Get(string name) => _resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;
}
