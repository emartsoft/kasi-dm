using Avalonia;
using Kasi.App.Startup;

namespace Kasi.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) =>
        AppStartup.Run(
            args,
            AppEndpoints.ForCurrentUser(),
            CompositionRoot.Build,
            session => BuildAvaloniaApp(session.Host.Services, session.StartHidden, session.Relay).StartWithClassicDesktopLifetime(args));

    internal static AppBuilder BuildAvaloniaApp(IServiceProvider services, bool startHidden = false, LaunchRelay? relay = null) =>
        AppBuilder.Configure(() => new App(services, startHidden, relay)).UsePlatformDetect();
}
