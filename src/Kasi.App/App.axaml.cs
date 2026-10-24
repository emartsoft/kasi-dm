using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Kasi.App.Browser;
using Kasi.App.Clipboard;
using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Resources;
using Kasi.App.Startup;
using Kasi.App.Tray;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Core;
using Kasi.Ipc.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kasi.App;

#pragma warning disable CA1001 // the tray model lives as long as the process; Quit ends the process
/// <param name="shell">True in the real app: the tray icon, close-to-tray and toasts. Tests leave it off (a headless platform has no tray).</param>
internal sealed class App(IServiceProvider services, bool startHidden = false, LaunchRelay? relay = null, BrowserServices? browser = null, bool shell = false) : Application, IAppLifetime
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private SettingsWindow? _settings;
    private AddCoordinator? _add;
    private TrayIconViewModel? _tray;
    private TrayIcon? _trayIcon;
    private IDisposable? _toastClicks;
    private ClipboardWatcher? _clipboard;
    private readonly Dictionary<JobId, DetailsWindow> _details = [];
    private bool _quitting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            if (services.GetService<IMediaPicker>() is AvaloniaMediaPicker mediaPicker)
            {
                mediaPicker.Owner = () => _window;
            }

            if (shell)
            {
                // closing the window hides it; only Quit ends Kasi, so a browser can always reach it
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            if (!startHidden)
            {
                ShowMainWindow();
            }

            _ = StartJobListAsync();
            ApplyTheme();
            services.GetRequiredService<ConfirmHostProxy>().Inner = new AvaloniaConfirmHost(() => _window);
            StartStatusPoll();
            if (shell)
            {
                StartShell();
            }

            relay?.Attach(request => Dispatcher.UIThread.Post(() => OnLaunchRequest(request)));
            if (browser is not null)
            {
                // the pipe has been serving since before this point: what it queued meanwhile is delivered now, on the UI thread
                _add = new AddCoordinator(
                    services.GetRequiredService<IJobService>(),
                    services.GetRequiredService<ISiteDefaults>(),
                    browser.Settings,
                    new AvaloniaAddDialogHost(),
                    new LinkRefresher(services.GetRequiredService<IJobService>(), browser.Refreshes),
                    ReportAddFault);
                _add.Watch();
                var main = services.GetRequiredService<MainWindowViewModel>();
                main.RefreshLinkHandler = _add.RequestRefresh;
                main.AddUrlHandler = _add.RequestAddBlank;
                main.DetailsRequested += row => ShowDetails(row, _add.RequestRefresh);
                main.SettingsHandler = () => ShowSettings(browser.Settings, SettingsSection.Browser);
                main.SchedulerHandler = () => ShowScheduler(services.GetRequiredService<IJobService>(), main.Sidebar.Selected?.Filter?.QueueId);
                main.ConnectedBrowsers = browser.ConnectedHosts;
                main.DiskFree = DiskFreeOfDownloads;
                if (shell)
                {
                    // a copied download link opens the Add dialog; the watcher listens only while the setting is on
                    _clipboard = new ClipboardWatcher(browser.Settings, new Win32ClipboardSource(), link => _add.RequestAdd([link]));
                    _clipboard.Start();
                }

                browser.Intake.Attach(_add, action => Dispatcher.UIThread.Post(action));
                browser.SettingsLauncher.Attach(section => Dispatcher.UIThread.Post(() => ShowSettings(browser.Settings, section)));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    // The engine started with the host, before the UI; the list loads what it holds and follows it. A failure leaves the list empty.
    private async Task StartJobListAsync()
    {
        try
        {
            await services.GetRequiredService<JobListViewModel>().StartAsync();
            await services.GetRequiredService<SidebarViewModel>().LoadAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the type only: the message may name a path
            if (services.GetService<ILoggerFactory>() is { } factory)
            {
                AppLog.JobListFailed(factory.CreateLogger<App>(), ex.GetType().Name);
            }
        }
    }

    private void OnLaunchRequest(AppArguments request)
    {
        if (request.Urls.Count > 0)
        {
            (_add as IAddDownloadService ?? services.GetRequiredService<IAddDownloadService>()).RequestAdd(request.Urls);
        }

        // the host relaunches `Kasi.exe --tray` on every reconnect: a bare --tray must stay quiet
        if (!request.Tray || request.Urls.Count > 0)
        {
            ShowMainWindow();
        }
    }

    // the saved theme now, and again whenever Settings saves a new one
    private void ApplyTheme()
    {
        var ui = services.GetRequiredService<IUiSettings>();
        ThemeApplier.Apply(this, ui.Current.Theme);
        ui.Changed += changed => Dispatcher.UIThread.Post(() => ThemeApplier.Apply(this, changed.Theme));
    }

    // one details window per download: asking again brings the open one forward
    private void ShowDetails(JobRowViewModel row, Action<JobId> refresh)
    {
        if (_details.TryGetValue(row.Id, out var open))
        {
            open.Activate();
            return;
        }

        var model = new DetailsViewModel(row, services.GetRequiredService<IJobService>(), services.GetRequiredService<IShell>()) { RefreshLinkHandler = refresh };
        var window = new DetailsWindow { DataContext = model };
        model.Closed += window.Close;
        window.Closed += (_, _) =>
        {
            _details.Remove(row.Id);
            model.Dispose();
        };
        _details[row.Id] = window;
        window.Show();
        _ = model.StartAsync();
    }

    // the status bar's one thing without an event: whether a browser extension is connected
    private void StartStatusPoll()
    {
        var main = services.GetRequiredService<MainWindowViewModel>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => main.Poll();
        timer.Start();
    }

    private void ReportAddFault(Exception ex)
    {
        // the type only: the message may name a URL or a path
        if (services.GetService<ILoggerFactory>() is { } factory)
        {
            AppLog.AddFailed(factory.CreateLogger<App>(), ex.GetType().Name);
        }
    }

    private void ShowScheduler(IJobService jobService, int? selectedQueueId)
    {
        var model = new SchedulerViewModel(jobService);
        var window = new SchedulerWindow { DataContext = model };
        _ = model.LoadAsync(selectedQueueId);
        window.Show();
    }

    /// <summary>The drive Downloads is on and the space left on it, for the status bar; null when the drive cannot be read (a disconnected share).</summary>
    private static (string Drive, long FreeBytes)? DiskFreeOfDownloads()
    {
        try
        {
            var root = Path.GetPathRoot(AppPaths.DownloadsFolder());
            return string.IsNullOrEmpty(root) ? null : (root.TrimEnd('\\'), new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void ShowSettings(IBrowserSettings settings, SettingsSection section)
    {
        if (_settings is not { IsVisible: true })
        {
            var categories = new CategoriesViewModel(
                services.GetRequiredService<ICategoryRepository>(),
                services.GetRequiredService<ICategoryRuleRepository>(),
                AppPaths.DownloadsFolder());
            var model = new SettingsViewModel(settings, services.GetRequiredService<IUiSettings>(), categories);
            _settings = new SettingsWindow { DataContext = model };
            _ = categories.LoadAsync();
            model.Saved += _settings.Close;
            _settings.Show();
        }

        ((SettingsViewModel)_settings.DataContext!).ShowSection(section);
        if (_settings.WindowState == WindowState.Minimized)
        {
            _settings.WindowState = WindowState.Normal;
        }

        _settings.Activate();
    }

    void IAppLifetime.ShowMainWindow() => ShowMainWindow();

    void IAppLifetime.Quit()
    {
        _quitting = true;
        _toastClicks?.Dispose();
        _clipboard?.Dispose();
        _trayIcon?.Dispose(); // so the icon does not stay in the notification area until the mouse passes over it
        _desktop!.Shutdown();
    }

    // The tray menu, the toasts and their clicks. The logic is in TrayIconViewModel, CompletionNotifier and NotificationRouter (all tested);
    // this only gives them an icon and a toast surface.
    private void StartShell()
    {
        var jobs = services.GetRequiredService<IJobService>();
        _tray = new TrayIconViewModel(jobs, this, limit: services.GetRequiredService<SpeedLimitState>());
        _tray.Start();
        _trayIcon = BuildTrayIcon(_tray);
        TrayIcon.SetIcons(this, [_trayIcon]);
        new CompletionNotifier(jobs, new WindowsToastService(), ReportAddFault, new WindowsShell()).Watch();
        _toastClicks = WindowsToastService.Activate(new NotificationRouter(jobs, new WindowsShell()));
    }

    private TrayIcon BuildTrayIcon(TrayIconViewModel model)
    {
        var limits = new NativeMenu();
        var presets = model.SpeedPresets
            .Select(preset => new NativeMenuItem(preset.Label)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = preset == model.SelectedPreset,
                Command = model.SetSpeedLimitCommand,
                CommandParameter = preset,
            })
            .ToList();
        presets.ForEach(limits.Add);
        var menu = new NativeMenu
        {
            new NativeMenuItem(model.OpenLabel) { Command = model.OpenCommand },
            new NativeMenuItemSeparator(),
            new NativeMenuItem(model.PauseAllLabel) { Command = model.PauseAllCommand },
            new NativeMenuItem(model.ResumeAllLabel) { Command = model.ResumeAllCommand },
            new NativeMenuItem(model.SpeedLimitLabel) { Menu = limits },
            new NativeMenuItemSeparator(),
            new NativeMenuItem(model.QuitLabel) { Command = model.QuitCommand },
        };
        using var icon = AssetLoader.Open(new Uri("avares://Kasi.App/Assets/kasi.png"));
        var trayIcon = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = model.ToolTip, Menu = menu };
        trayIcon.Clicked += (_, _) => ShowMainWindow();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrayIconViewModel.ToolTip))
            {
                trayIcon.ToolTipText = model.ToolTip;
            }
            else if (e.PropertyName == nameof(TrayIconViewModel.SelectedPreset))
            {
                for (var i = 0; i < presets.Count; i++)
                {
                    presets[i].IsChecked = model.SpeedPresets[i] == model.SelectedPreset;
                }
            }
        };
        return trayIcon;
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting || _window is null)
        {
            return;
        }

        e.Cancel = true; // to the tray, not out of the process
        _window.Hide();
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            _window.Closed += (_, _) => _window = null;
            if (shell)
            {
                _window.Closing += OnMainWindowClosing;
            }

            _desktop!.MainWindow = _window;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }
}
#pragma warning restore CA1001
