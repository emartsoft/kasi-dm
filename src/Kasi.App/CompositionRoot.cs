using Kasi.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kasi.App;

/// <summary>Wires the engine, persistence and view models together (TRD 5.2). Engine services are registered here as they appear.</summary>
internal static class CompositionRoot
{
    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders(); // Serilog replaces this when logging lands; nothing may reach a console in a WinExe

        builder.Services.AddSingleton<MainWindowViewModel>();

        return builder.Build();
    }
}
