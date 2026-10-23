using Avalonia;
using Kasi.App.Browser;
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
            session => BuildAvaloniaApp(session.Host.Services, session.StartHidden, session.Relay, session.Browser, shell: true).StartWithClassicDesktopLifetime(args));

    internal static AppBuilder BuildAvaloniaApp(IServiceProvider services, bool startHidden = false, LaunchRelay? relay = null, BrowserServices? browser = null, bool shell = false) =>
        AppBuilder.Configure(() => new App(services, startHidden, relay, browser, shell)).UsePlatformDetect();
}
