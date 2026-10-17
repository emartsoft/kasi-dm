using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Kasi.App;

internal sealed class App(IServiceProvider services) : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = services.GetRequiredService<MainWindowViewModel>() };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
