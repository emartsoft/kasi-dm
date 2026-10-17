using Avalonia;
using Avalonia.Headless;
using Kasi.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Kasi.App.Tests;

public static class TestAppBuilder
{
    // The same composition root as the real app, with the headless platform instead of Win32.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new App(CompositionRoot.Build([]).Services)).UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
