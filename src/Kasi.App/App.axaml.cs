using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Kasi.App.Startup;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Kasi.App;

internal sealed class App(IServiceProvider services, bool startHidden = false, LaunchRelay? relay = null) : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;

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

            relay?.Attach(request => Dispatcher.UIThread.Post(() => OnLaunchRequest(request)));
        }

        base.OnFrameworkInitializationCompleted();
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
}
