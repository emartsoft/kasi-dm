using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Kasi.App.Browser;
using Kasi.App.Startup;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Ipc.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kasi.App;

internal sealed class App(IServiceProvider services, bool startHidden = false, LaunchRelay? relay = null, BrowserServices? browser = null) : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private SettingsWindow? _settings;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            if (!startHidden)
            {
                ShowMainWindow();
            }

            _ = StartJobListAsync();
            relay?.Attach(request => Dispatcher.UIThread.Post(() => OnLaunchRequest(request)));
            if (browser is not null)
            {
                // the pipe has been serving since before this point: what it queued meanwhile is delivered now, on the UI thread
                browser.Intake.Attach(new WindowSink(services.GetRequiredService<MainWindowViewModel>(), ShowMainWindow), action => Dispatcher.UIThread.Post(action));
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
            services.GetRequiredService<IAddDownloadService>().RequestAdd(request.Urls);
        }

        // the host relaunches `Kasi.exe --tray` on every reconnect: a bare --tray must stay quiet
        if (!request.Tray || request.Urls.Count > 0)
        {
            ShowMainWindow();
        }
    }

    private void ShowSettings(IBrowserSettings settings, SettingsSection section)
    {
        if (_settings is not { IsVisible: true })
        {
            var model = new SettingsViewModel(settings);
            _settings = new SettingsWindow { DataContext = model };
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

    private void ShowMainWindow()
    {
        if (_window is not { IsVisible: true })
        {
            _window = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
            _desktop!.MainWindow = _window;
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    /// <summary>Hands a browser request to the main window's view model and brings the window up (the dialogs of PR 2.8d replace this).</summary>
    private sealed class WindowSink(IBrowserRequestSink inner, Action raise) : IBrowserRequestSink
    {
        public void ShowAdd(BrowserDownload download)
        {
            inner.ShowAdd(download);
            raise();
        }

        public void ShowPicker(BrowserBatch batch)
        {
            inner.ShowPicker(batch);
            raise();
        }
    }
}
