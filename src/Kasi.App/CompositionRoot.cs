using Kasi.App.Engine;
using Kasi.App.Notifications;
using Kasi.App.Startup;
using Kasi.App.ViewModels;
using Kasi.Core;
using Kasi.Data;
using Kasi.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kasi.App;

/// <summary>Wires the engine, persistence and view models together (TRD 5.2). Engine services are registered here as they appear.</summary>
internal static class CompositionRoot
{
    public static IHost Build(string[] args) => Build(args, null);

    /// <summary><paramref name="dataFolder"/> holds the database; null means the user's local data folder (tests pass a temporary one).</summary>
    public static IHost Build(string[] args, string? dataFolder)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders(); // Serilog replaces this when logging lands; nothing may reach a console in a WinExe

        var database = dataFolder is null
            ? AppPaths.DatabasePath(Environment.GetEnvironmentVariable(AppEndpoints.TestSuffixVariable))
            : Path.Combine(dataFolder, "kasi.db");
        AddEngine(builder.Services, database);
        builder.Services.AddSingleton<IUiSettings>(_ => new UiSettingsStore(Path.Combine(Path.GetDirectoryName(database)!, "ui-settings.json")));
        builder.Services.AddSingleton(sp => new SpeedLimitState(sp.GetRequiredService<IJobService>()));
        builder.Services.AddSingleton<IShell, WindowsShell>();
        builder.Services.AddSingleton<ConfirmHostProxy>();
        builder.Services.AddSingleton<IConfirmHost>(sp => sp.GetRequiredService<ConfirmHostProxy>());
        builder.Services.AddSingleton<JobListViewModel>();
        builder.Services.AddSingleton<SidebarViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();
        builder.Services.AddSingleton<IAddDownloadService>(sp => sp.GetRequiredService<MainWindowViewModel>()); // PR 2.8d-2 registers the Add dialog instead

        return builder.Build();
    }

    // Nothing here touches the disk until the host starts: the connection factory only remembers the path.
    private static void AddEngine(IServiceCollection services, string database)
    {
        services.AddSingleton(_ => new SqliteConnectionFactory(database));
        services.AddSingleton<ISiteDefaults>(_ => new SiteDefaultsStore(Path.Combine(Path.GetDirectoryName(database)!, "site-defaults.json")));
        services.AddSingleton<MigrationRunner>(sp => new MigrationRunner(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton<IJobRepository>(sp => new JobRepository(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton<ISegmentRepository>(sp => new SegmentRepository(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton<ICategoryRepository>(sp => new CategoryRepository(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton<IQueueRepository>(sp => new QueueRepository(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton<IJobSecretRepository>(sp => new JobSecretRepository(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton(sp => new CheckpointWriter(sp.GetRequiredService<SqliteConnectionFactory>()));
        services.AddSingleton<ICheckpointWriter>(sp => sp.GetRequiredService<CheckpointWriter>());
        services.AddSingleton(_ => new BandwidthLimiter());
        services.AddSingleton(sp => new DownloadEngine(
            sp.GetRequiredService<IJobRepository>(),
            sp.GetRequiredService<ISegmentRepository>(),
            sp.GetRequiredService<ICheckpointWriter>(),
            sp.GetRequiredService<BandwidthLimiter>(),
            secrets: sp.GetRequiredService<IJobSecretRepository>()));
        services.AddSingleton<IJobService>(sp => new JobService(
            sp.GetRequiredService<DownloadEngine>(),
            sp.GetRequiredService<IJobRepository>(),
            sp.GetRequiredService<ICategoryRepository>(),
            sp.GetRequiredService<IQueueRepository>(),
            AppPaths.DownloadsFolder(),
            sp.GetRequiredService<IJobSecretRepository>(),
            sp.GetRequiredService<BandwidthLimiter>()));
        services.AddSingleton<IHostedService, EngineLifetime>();
    }
}
