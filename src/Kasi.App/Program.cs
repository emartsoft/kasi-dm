using Avalonia;
using Microsoft.Extensions.Hosting;

namespace Kasi.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var host = CompositionRoot.Build(args);
        host.Start();

        return BuildAvaloniaApp(host.Services).StartWithClassicDesktopLifetime(args);
    }

    internal static AppBuilder BuildAvaloniaApp(IServiceProvider services) =>
        AppBuilder.Configure(() => new App(services)).UsePlatformDetect();
}
