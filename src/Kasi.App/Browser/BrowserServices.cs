using Kasi.Ipc.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kasi.App.Browser;

/// <summary>
/// Everything the browser pipe needs, created before the host and the UI exist (the pipe opens first, ADR 0023): the settings, the intake
/// the UI attaches to later, the Settings launcher and the message handler. The UI side reaches them through <see cref="AppSession"/>.
/// </summary>
internal sealed class BrowserServices
{
    public BrowserServices(IBrowserSettings settings, TimeProvider? time = null, ILogger? logger = null)
    {
        Settings = settings;
        Intake = new BrowserIntake();
        SettingsLauncher = new SettingsLauncher();
        Refreshes = new RefreshBroker(time);
        Media = new BrowserMediaServices();
        Handler = new BrowserMessageHandler(settings, Intake, SettingsLauncher, time, logger, Refreshes, Media);
        Logger = logger ?? NullLogger.Instance;
    }

    public IBrowserSettings Settings { get; }

    public BrowserIntake Intake { get; }

    public SettingsLauncher SettingsLauncher { get; }

    /// <summary>Detected streams and the capture implementation attached once the DI host is running.</summary>
    public BrowserMediaServices Media { get; }

    public BrowserMessageHandler Handler { get; }

    /// <summary>Asks the connected browsers for a fresh link (TRD 6.6); bound to the pipe by start-up.</summary>
    public RefreshBroker Refreshes { get; }

    /// <summary>How many browser hosts are connected now; start-up points it at the pipe. The status bar shows it.</summary>
    public Func<int> ConnectedHosts { get; set; } = () => 0;

    private ILogger Logger { get; }

    /// <summary>The services of the real app: settings in the user's local data folder, named apart for a test instance.</summary>
    public static BrowserServices ForCurrentUser(string? endpointSuffix, ILogger? logger = null) =>
        new(new BrowserSettingsStore(BrowserSettingsStore.DefaultPath(endpointSuffix)), logger: logger);

    /// <summary>Pushes the rules to every connected extension whenever the settings change. Dispose to stop.</summary>
    public IDisposable PublishChanges(KasiPipeServer server)
    {
        void OnChanged(BrowserSettings changed) => _ = PushAsync(server);
        Settings.Changed += OnChanged;
        return new Subscription(() => Settings.Changed -= OnChanged);
    }

    private async Task PushAsync(KasiPipeServer server)
    {
        try
        {
            await Handler.PushRulesAsync(server, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // the server is closing
        }
    }

    private sealed class Subscription(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
