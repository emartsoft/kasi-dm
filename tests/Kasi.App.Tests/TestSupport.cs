using System.Security.Principal;
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
