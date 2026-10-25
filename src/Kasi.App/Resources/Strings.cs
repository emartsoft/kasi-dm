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

    public static string SelectRow => Get(nameof(SelectRow));

    public static string SettingsScanDownloads => Get(nameof(SettingsScanDownloads));

    public static string SettingsScanDownloadsDescription => Get(nameof(SettingsScanDownloadsDescription));

    public static string SchedulerTitle => Get(nameof(SchedulerTitle));

    public static string SchedulerQueueLabel => Get(nameof(SchedulerQueueLabel));

    public static string SchedulerSelectQueue => Get(nameof(SchedulerSelectQueue));

    public static string SchedulerTabQueue => Get(nameof(SchedulerTabQueue));

    public static string SchedulerTabSchedule => Get(nameof(SchedulerTabSchedule));

    public static string SchedulerTabPost => Get(nameof(SchedulerTabPost));

    public static string SchedulerMaxParallel => Get(nameof(SchedulerMaxParallel));

    public static string SchedulerMaxParallelName => Get(nameof(SchedulerMaxParallelName));

    public static string SchedulerEnable => Get(nameof(SchedulerEnable));

    public static string SchedulerStartLabel => Get(nameof(SchedulerStartLabel));

    public static string SchedulerStartName => Get(nameof(SchedulerStartName));

    public static string SchedulerStopLabel => Get(nameof(SchedulerStopLabel));

    public static string SchedulerStopName => Get(nameof(SchedulerStopName));

    public static string SchedulerUntilEmpty => Get(nameof(SchedulerUntilEmpty));

    public static string SchedulerUntilEmptyName => Get(nameof(SchedulerUntilEmptyName));

    public static string SchedulerActiveDays => Get(nameof(SchedulerActiveDays));

    public static string SchedulerDaySun => Get(nameof(SchedulerDaySun));

    public static string SchedulerDayMon => Get(nameof(SchedulerDayMon));

    public static string SchedulerDayTue => Get(nameof(SchedulerDayTue));

    public static string SchedulerDayWed => Get(nameof(SchedulerDayWed));

    public static string SchedulerDayThu => Get(nameof(SchedulerDayThu));

    public static string SchedulerDayFri => Get(nameof(SchedulerDayFri));

    public static string SchedulerDaySat => Get(nameof(SchedulerDaySat));

    public static string SchedulerDaySunday => Get(nameof(SchedulerDaySunday));

    public static string SchedulerDayMonday => Get(nameof(SchedulerDayMonday));

    public static string SchedulerDayTuesday => Get(nameof(SchedulerDayTuesday));

    public static string SchedulerDayWednesday => Get(nameof(SchedulerDayWednesday));

    public static string SchedulerDayThursday => Get(nameof(SchedulerDayThursday));

    public static string SchedulerDayFriday => Get(nameof(SchedulerDayFriday));

    public static string SchedulerDaySaturday => Get(nameof(SchedulerDaySaturday));

    public static string SchedulerWhenDone => Get(nameof(SchedulerWhenDone));

    public static string SchedulerPostActionName => Get(nameof(SchedulerPostActionName));

    public static string SchedulerForce => Get(nameof(SchedulerForce));

    public static string SchedulerForceName => Get(nameof(SchedulerForceName));

    public static string SchedulerProgramPath => Get(nameof(SchedulerProgramPath));

    public static string SchedulerProgramPathName => Get(nameof(SchedulerProgramPathName));

    public static string SchedulerProgramArgs => Get(nameof(SchedulerProgramArgs));

    public static string SchedulerProgramArgsName => Get(nameof(SchedulerProgramArgsName));

    public static string SchedulerSave => Get(nameof(SchedulerSave));

    public static string SchedulerCancel => Get(nameof(SchedulerCancel));

    public static string SchedulerErrParallel => Get(nameof(SchedulerErrParallel));

    public static string SchedulerErrStart => Get(nameof(SchedulerErrStart));

    public static string SchedulerErrStop => Get(nameof(SchedulerErrStop));

    public static string PostActionNone => Get(nameof(PostActionNone));

    public static string PostActionSleep => Get(nameof(PostActionSleep));

    public static string PostActionHibernate => Get(nameof(PostActionHibernate));

    public static string PostActionShutdown => Get(nameof(PostActionShutdown));

    public static string PostActionExit => Get(nameof(PostActionExit));

    public static string PostActionRunProgram => Get(nameof(PostActionRunProgram));

    public static string DetailsWhenCompleted => Get(nameof(DetailsWhenCompleted));

    public static string DetailsChangeQueueAction => Get(nameof(DetailsChangeQueueAction));

    public static string SettingsPageGeneral => Get(nameof(SettingsPageGeneral));

    public static string SettingsPageGeneralDescription => Get(nameof(SettingsPageGeneralDescription));

    public static string SettingsPageBrowser => Get(nameof(SettingsPageBrowser));

    public static string SettingsPageBrowserDescription => Get(nameof(SettingsPageBrowserDescription));

    public static string SettingsPageFiles => Get(nameof(SettingsPageFiles));

    public static string SettingsPageFilesDescription => Get(nameof(SettingsPageFilesDescription));

    public static string SettingsPageScheduler => Get(nameof(SettingsPageScheduler));

    public static string SettingsPageSchedulerDescription => Get(nameof(SettingsPageSchedulerDescription));

    public static string SettingsPageAbout => Get(nameof(SettingsPageAbout));

    public static string SettingsPageAboutDescription => Get(nameof(SettingsPageAboutDescription));

    public static string SettingsBreadcrumbRoot => Get(nameof(SettingsBreadcrumbRoot));

    public static string SettingsFind => Get(nameof(SettingsFind));

    public static string SettingsFindName => Get(nameof(SettingsFindName));

    public static string SettingsRestoreDefaults => Get(nameof(SettingsRestoreDefaults));

    public static string SettingsReset => Get(nameof(SettingsReset));

    public static string SettingsNavigation => Get(nameof(SettingsNavigation));

    public static string SettingsThisPc => Get(nameof(SettingsThisPc));

    public static string SettingsDataFolder => Get(nameof(SettingsDataFolder));

    public static string SettingsVersion => Get(nameof(SettingsVersion));

    public static string SettingsOpenDataFolder => Get(nameof(SettingsOpenDataFolder));

    public static string SettingsOpenScheduler => Get(nameof(SettingsOpenScheduler));

    public static string SettingsRulesHeading => Get(nameof(SettingsRulesHeading));

    public static string SettingsRulesNote => Get(nameof(SettingsRulesNote));

    public static string SettingsAutoSaved => Get(nameof(SettingsAutoSaved));

    public static string SettingsRestartNote => Get(nameof(SettingsRestartNote));

    public static string AboutTagline => Get(nameof(AboutTagline));

    public static string AboutOwner => Get(nameof(AboutOwner));

    public static string AboutNotices => Get(nameof(AboutNotices));

    public static string SettingsPageNameLabel => Get(nameof(SettingsPageNameLabel));

    public static string DetailsPeak => Get(nameof(DetailsPeak));

    public static string DetailsElapsed => Get(nameof(DetailsElapsed));

    public static string DetailsChart => Get(nameof(DetailsChart));

    public static string DetailsCopyLink => Get(nameof(DetailsCopyLink));

    public static string DetailsIdle => Get(nameof(DetailsIdle));

    public static string DetailsStreamsActive => Get(nameof(DetailsStreamsActive));

    public static string DetailsActiveStreams => Get(nameof(DetailsActiveStreams));

    public static string DetailsMatrix => Get(nameof(DetailsMatrix));

    public static string DetailsOpenWhenCompleted => Get(nameof(DetailsOpenWhenCompleted));

    public static string DetailsSpeedLimit => Get(nameof(DetailsSpeedLimit));

    public static string JobColumnProgress => Get(nameof(JobColumnProgress));

    public static string JobColumnSize => Get(nameof(JobColumnSize));

    public static string AddTitle => Get(nameof(AddTitle));

    public static string AddAutomationName => Get(nameof(AddAutomationName));

    public static string AddUrlLabel => Get(nameof(AddUrlLabel));

    public static string AddFileNameLabel => Get(nameof(AddFileNameLabel));

    public static string AddFolderLabel => Get(nameof(AddFolderLabel));

    public static string AddCategoryLabel => Get(nameof(AddCategoryLabel));

    public static string PickerCategoryAutomatic => Get(nameof(PickerCategoryAutomatic));

    public static string MediaPickerTitle => Get(nameof(MediaPickerTitle));

    public static string MediaPickerAutomationName => Get(nameof(MediaPickerAutomationName));

    public static string MediaPickerQualityLabel => Get(nameof(MediaPickerQualityLabel));

    public static string MediaPickerFormatLabel => Get(nameof(MediaPickerFormatLabel));

    public static string MediaPickerSubtitlesLabel => Get(nameof(MediaPickerSubtitlesLabel));

    public static string MediaPickerNoSubtitles => Get(nameof(MediaPickerNoSubtitles));

    public static string MediaPickerAdd => Get(nameof(MediaPickerAdd));

    public static string MediaPickerCancel => Get(nameof(MediaPickerCancel));

    public static string MediaPickerAudioLabel => Get(nameof(MediaPickerAudioLabel));

    public static string MediaPickerAudioOnly => Get(nameof(MediaPickerAudioOnly));

    public static string MediaPickerMp4 => Get(nameof(MediaPickerMp4));

    public static string MediaPickerMkv => Get(nameof(MediaPickerMkv));


    public static string MediaPickerSubtitle => Get(nameof(MediaPickerSubtitle));

    public static string MediaPickerNoQuality => Get(nameof(MediaPickerNoQuality));

    public static string MediaPickerNoAudio => Get(nameof(MediaPickerNoAudio));

    public static string MediaPickerUnknownCodec => Get(nameof(MediaPickerUnknownCodec));

    public static string MediaPickerUnknownBitrate => Get(nameof(MediaPickerUnknownBitrate));

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

    public static string RefreshTitle => Get(nameof(RefreshTitle));

    public static string RefreshAutomationName => Get(nameof(RefreshAutomationName));

    public static string RefreshAsk => Get(nameof(RefreshAsk));

    public static string RefreshStart => Get(nameof(RefreshStart));

    public static string RefreshWaiting => Get(nameof(RefreshWaiting));

    public static string RefreshDone => Get(nameof(RefreshDone));

    public static string RefreshNoReferrer => Get(nameof(RefreshNoReferrer));

    public static string RefreshNoBrowser => Get(nameof(RefreshNoBrowser));

    public static string RefreshTimedOut => Get(nameof(RefreshTimedOut));

    public static string RefreshRejected => Get(nameof(RefreshRejected));

    public static string RefreshFailed => Get(nameof(RefreshFailed));

    public static string RefreshClose => Get(nameof(RefreshClose));

    public static string MenuRefreshLink => Get(nameof(MenuRefreshLink));

    public static string TrayOpen => Get(nameof(TrayOpen));

    public static string TrayPauseAll => Get(nameof(TrayPauseAll));

    public static string TrayResumeAll => Get(nameof(TrayResumeAll));

    public static string TraySpeedLimit => Get(nameof(TraySpeedLimit));

    public static string TrayUnlimited => Get(nameof(TrayUnlimited));

    public static string TrayLimitPreset => Get(nameof(TrayLimitPreset));

    public static string TrayQuit => Get(nameof(TrayQuit));

    public static string TrayFlyoutName => Get(nameof(TrayFlyoutName));

    public static string TrayFlyoutSpeed => Get(nameof(TrayFlyoutSpeed));

    public static string TrayFlyoutLimit => Get(nameof(TrayFlyoutLimit));

    public static string ToastDetailsFormat => Get(nameof(ToastDetailsFormat));

    public static string TrayToolTipSpeed => Get(nameof(TrayToolTipSpeed));

    public static string ToastCompleted => Get(nameof(ToastCompleted));

    public static string ToastFailed => Get(nameof(ToastFailed));

    public static string ToastOpenFile => Get(nameof(ToastOpenFile));

    public static string ToastOpenFolder => Get(nameof(ToastOpenFolder));

    public static string BrowserClipboardCapture => Get(nameof(BrowserClipboardCapture));

    public static string ToolbarAddUrl => Get(nameof(ToolbarAddUrl));

    public static string ToolbarResume => Get(nameof(ToolbarResume));

    public static string ToolbarPause => Get(nameof(ToolbarPause));

    public static string ToolbarStop => Get(nameof(ToolbarStop));

    public static string ToolbarDelete => Get(nameof(ToolbarDelete));

    public static string ToolbarDeleteCompleted => Get(nameof(ToolbarDeleteCompleted));

    public static string ToolbarStartQueue => Get(nameof(ToolbarStartQueue));

    public static string ToolbarStopQueue => Get(nameof(ToolbarStopQueue));

    public static string ToolbarScheduler => Get(nameof(ToolbarScheduler));

    public static string ToolbarSpeedLimit => Get(nameof(ToolbarSpeedLimit));

    public static string ToolbarSettings => Get(nameof(ToolbarSettings));

    public static string ToolbarAutomationName => Get(nameof(ToolbarAutomationName));

    public static string SidebarAutomationName => Get(nameof(SidebarAutomationName));

    public static string SidebarStatus => Get(nameof(SidebarStatus));

    public static string SidebarCategories => Get(nameof(SidebarCategories));

    public static string SidebarQueues => Get(nameof(SidebarQueues));

    public static string FilterAll => Get(nameof(FilterAll));

    public static string FilterActive => Get(nameof(FilterActive));

    public static string FilterFinished => Get(nameof(FilterFinished));

    public static string FilterUnfinished => Get(nameof(FilterUnfinished));

    public static string JobColumnSpeed => Get(nameof(JobColumnSpeed));

    public static string JobColumnTimeLeft => Get(nameof(JobColumnTimeLeft));

    public static string JobColumnDateAdded => Get(nameof(JobColumnDateAdded));

    public static string JobColumnCategory => Get(nameof(JobColumnCategory));

    public static string StateQueued => Get(nameof(StateQueued));

    public static string StateProbing => Get(nameof(StateProbing));

    public static string StateDownloading => Get(nameof(StateDownloading));

    public static string StateRetrying => Get(nameof(StateRetrying));

    public static string StatePaused => Get(nameof(StatePaused));

    public static string StateMuxing => Get(nameof(StateMuxing));

    public static string StateVerifying => Get(nameof(StateVerifying));

    public static string StateCompleted => Get(nameof(StateCompleted));

    public static string StateFailed => Get(nameof(StateFailed));

    public static string SearchPlaceholder => Get(nameof(SearchPlaceholder));

    public static string SearchAutomationName => Get(nameof(SearchAutomationName));

    public static string StatusTotalSpeed => Get(nameof(StatusTotalSpeed));

    public static string StatusCounts => Get(nameof(StatusCounts));

    public static string StatusLimit => Get(nameof(StatusLimit));

    public static string StatusUnlimited => Get(nameof(StatusUnlimited));

    public static string StatusExtensionConnected => Get(nameof(StatusExtensionConnected));

    public static string StatusExtensionNone => Get(nameof(StatusExtensionNone));

    public static string StatusBarAutomationName => Get(nameof(StatusBarAutomationName));

    public static string TimeSeconds => Get(nameof(TimeSeconds));

    public static string TimeMinutes => Get(nameof(TimeMinutes));

    public static string TimeHoursMinutes => Get(nameof(TimeHoursMinutes));

    public static string TimeDays => Get(nameof(TimeDays));

    public static string DateToday => Get(nameof(DateToday));

    public static string DateYesterday => Get(nameof(DateYesterday));

    public static string DeleteTitle => Get(nameof(DeleteTitle));

    public static string DeleteMessageOne => Get(nameof(DeleteMessageOne));

    public static string DeleteMessageMany => Get(nameof(DeleteMessageMany));

    public static string DeleteCompletedMessage => Get(nameof(DeleteCompletedMessage));

    public static string DeleteAlsoFiles => Get(nameof(DeleteAlsoFiles));

    public static string DeleteDontAsk => Get(nameof(DeleteDontAsk));

    public static string DeleteButton => Get(nameof(DeleteButton));

    public static string ColumnsMenu => Get(nameof(ColumnsMenu));

    public static string ColumnsReset => Get(nameof(ColumnsReset));

    public static string ThemeLabel => Get(nameof(ThemeLabel));

    public static string ThemeSystem => Get(nameof(ThemeSystem));

    public static string ThemeLight => Get(nameof(ThemeLight));

    public static string ThemeDark => Get(nameof(ThemeDark));

    public static string DetailsTitle => Get(nameof(DetailsTitle));

    public static string DetailsAutomationName => Get(nameof(DetailsAutomationName));

    public static string DetailsDownloaded => Get(nameof(DetailsDownloaded));

    public static string DetailsSpeed => Get(nameof(DetailsSpeed));

    public static string DetailsTimeLeft => Get(nameof(DetailsTimeLeft));

    public static string DetailsResume => Get(nameof(DetailsResume));

    public static string DetailsResumeSupported => Get(nameof(DetailsResumeSupported));

    public static string DetailsResumeNotSupported => Get(nameof(DetailsResumeNotSupported));

    public static string DetailsSavingTo => Get(nameof(DetailsSavingTo));

    public static string DetailsSegmentMap => Get(nameof(DetailsSegmentMap));

    public static string DetailsMapSummary => Get(nameof(DetailsMapSummary));

    public static string DetailsMapHint => Get(nameof(DetailsMapHint));

    public static string DetailsColConnection => Get(nameof(DetailsColConnection));

    public static string DetailsColRange => Get(nameof(DetailsColRange));

    public static string DetailsColSpeed => Get(nameof(DetailsColSpeed));

    public static string DetailsColState => Get(nameof(DetailsColState));

    public static string DetailsReceiving => Get(nameof(DetailsReceiving));

    public static string DetailsLog => Get(nameof(DetailsLog));

    public static string DetailsRefreshLink => Get(nameof(DetailsRefreshLink));

    public static string DetailsOpenFolder => Get(nameof(DetailsOpenFolder));

    public static string DetailsPause => Get(nameof(DetailsPause));

    public static string DetailsResumeButton => Get(nameof(DetailsResumeButton));

    public static string DetailsClose => Get(nameof(DetailsClose));

    public static string MenuDetails => Get(nameof(MenuDetails));

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

    public static string CategoriesTab => Get(nameof(CategoriesTab));

    public static string CategoriesNote => Get(nameof(CategoriesNote));

    public static string CategoriesListLabel => Get(nameof(CategoriesListLabel));

    public static string CategoryNameLabel => Get(nameof(CategoryNameLabel));

    public static string CategoryFolderLabel => Get(nameof(CategoryFolderLabel));

    public static string CategoryAddButton => Get(nameof(CategoryAddButton));

    public static string CategorySaveButton => Get(nameof(CategorySaveButton));

    public static string CategoryDeleteButton => Get(nameof(CategoryDeleteButton));

    public static string CategoryNewLabel => Get(nameof(CategoryNewLabel));

    public static string CategoryNewFolderLabel => Get(nameof(CategoryNewFolderLabel));

    public static string CategoryBuiltinMark => Get(nameof(CategoryBuiltinMark));

    public static string RulesLabel => Get(nameof(RulesLabel));

    public static string RuleKindLabel => Get(nameof(RuleKindLabel));

    public static string RuleKindExtension => Get(nameof(RuleKindExtension));

    public static string RuleKindDomain => Get(nameof(RuleKindDomain));

    public static string RuleKindUrl => Get(nameof(RuleKindUrl));

    public static string RulePatternLabel => Get(nameof(RulePatternLabel));

    public static string RuleAddButton => Get(nameof(RuleAddButton));

    public static string RuleDeleteButton => Get(nameof(RuleDeleteButton));

    public static string RuleMoveUp => Get(nameof(RuleMoveUp));

    public static string RuleMoveDown => Get(nameof(RuleMoveDown));

    public static string RuleEnabledLabel => Get(nameof(RuleEnabledLabel));

    public static string RuleOriginYours => Get(nameof(RuleOriginYours));

    public static string TryLabel => Get(nameof(TryLabel));

    public static string TryButton => Get(nameof(TryButton));

    public static string TryNoLink => Get(nameof(TryNoLink));

    public static string TryResultFormat => Get(nameof(TryResultFormat));

    public static string TryBecauseRule => Get(nameof(TryBecauseRule));

    public static string TryBecauseNothing => Get(nameof(TryBecauseNothing));

    public static string CategoriesSaved => Get(nameof(CategoriesSaved));

    public static string CategoryErrorName => Get(nameof(CategoryErrorName));

    public static string CategoryErrorNameTaken => Get(nameof(CategoryErrorNameTaken));

    public static string CategoryErrorFolder => Get(nameof(CategoryErrorFolder));

    public static string RuleErrorEmpty => Get(nameof(RuleErrorEmpty));

    public static string RuleErrorTooLong => Get(nameof(RuleErrorTooLong));

    public static string RuleErrorEverything => Get(nameof(RuleErrorEverything));

    public static string RuleErrorExtension => Get(nameof(RuleErrorExtension));

    public static string RuleErrorChars => Get(nameof(RuleErrorChars));

    public static string StatusDiskFree => Get(nameof(StatusDiskFree));

    public static string StatusDown => Get(nameof(StatusDown));

    public static string SidebarAggregateSpeed => Get(nameof(SidebarAggregateSpeed));

    public static string SidebarExtension => Get(nameof(SidebarExtension));

    public static string SidebarExtensionConnected => Get(nameof(SidebarExtensionConnected));

    public static string SidebarExtensionNone => Get(nameof(SidebarExtensionNone));

    public static string SearchShortcut => Get(nameof(SearchShortcut));

    public static string AppTagline => Get(nameof(AppTagline));

    public static string QueueFrom => Get(nameof(QueueFrom));

    public static string SchedulerQuota => Get(nameof(SchedulerQuota));

    public static string SchedulerQuotaEnable => Get(nameof(SchedulerQuotaEnable));

    public static string SchedulerQuotaEnableName => Get(nameof(SchedulerQuotaEnableName));

    public static string SchedulerQuotaSizeName => Get(nameof(SchedulerQuotaSizeName));

    public static string SchedulerQuotaUnit => Get(nameof(SchedulerQuotaUnit));

    public static string SchedulerQuotaResetName => Get(nameof(SchedulerQuotaResetName));

    public static string SchedulerQuotaDaily => Get(nameof(SchedulerQuotaDaily));

    public static string SchedulerQuotaWeekly => Get(nameof(SchedulerQuotaWeekly));

    public static string SchedulerQuotaNever => Get(nameof(SchedulerQuotaNever));

    public static string SchedulerQuotaSoft => Get(nameof(SchedulerQuotaSoft));

    public static string SchedulerErrQuota => Get(nameof(SchedulerErrQuota));

    public static string SchedulerWakeWarning => Get(nameof(SchedulerWakeWarning));
}
