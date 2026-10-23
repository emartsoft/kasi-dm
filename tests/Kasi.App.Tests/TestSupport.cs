using System.Security.Principal;
using Kasi.App.Browser;
using Kasi.App.Startup;
using Microsoft.Extensions.Logging;

namespace Kasi.App.Tests;

internal static class TestEndpoints
{
    /// <summary>Endpoint names no real Kasi on the machine uses, unique per call.</summary>
    public static AppEndpoints New()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return AppEndpoints.For(identity.User!.Value, Guid.NewGuid().ToString("N"));
    }
}

internal sealed class CapturingLogger : ILogger
{
    public List<string> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines)
        {
            Lines.Add(formatter(state, exception));
        }
    }
}

internal static class TestBrowser
{
    /// <summary>Browser services whose settings file is a fresh temporary one, so a test never reads or writes a developer's real settings.</summary>
    public static BrowserServices New(TimeProvider? time = null, ILogger? logger = null) => new(NewStore(), time, logger);

    public static async Task<BrowserServices> NewAsync(BrowserSettings initial, TimeProvider? time = null, ILogger? logger = null)
    {
        var store = NewStore();
        await store.SaveAsync(initial, CancellationToken.None);
        return new BrowserServices(store, time, logger);
    }

    public static BrowserSettingsStore NewStore() =>
        new(Path.Combine(Path.GetTempPath(), "kasi-test-" + Guid.NewGuid().ToString("N"), "browser-settings.json"));
}

internal static class TestHosts
{
    /// <summary>The real composition root with a database of its own, so hosts started by parallel tests never share one file.</summary>
    public static Microsoft.Extensions.Hosting.IHost Build(string[] args) =>
        CompositionRoot.Build(args, Path.Combine(Path.GetTempPath(), "kasi-app-tests-" + Guid.NewGuid().ToString("N")));
}
