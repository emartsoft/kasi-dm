using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kasi.App.Engine;
using Kasi.App.Resources;
using Kasi.App.Threading;
using Kasi.App.ViewModels;
using Kasi.Core;

namespace Kasi.App.Tray;

internal sealed record SpeedPreset(string Label, long? BytesPerSecond);

/// <summary>What the tray needs from the application: show the window, and end the process.</summary>
internal interface IAppLifetime
{
    void ShowMainWindow();

    /// <summary>Exits completely. The browser then reconnects only on a download or a click (decision 4 of the M2.8 plan).</summary>
    void Quit();
}

/// <summary>
/// The tray menu's logic (KASI-UI-004): total speed, Pause all, Resume all, speed-limit presets, Open Kasi and Quit. It needs no window and
/// no native icon, so TC-UI-003 runs its actions against a service with the main window closed. The icon is only a view of this class.
/// </summary>
internal sealed partial class TrayIconViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly CompositeFormat _toolTipFormat = CompositeFormat.Parse(Strings.TrayToolTipSpeed);
    private static readonly CompositeFormat _presetFormat = CompositeFormat.Parse(Strings.TrayLimitPreset);

    private readonly IJobService _jobs;
    private readonly IAppLifetime _app;
    private readonly Action<Action> _post;
    private readonly Lock _gate = new();
    private readonly Dictionary<JobId, long> _speeds = [];
    private readonly UiBatchDispatcher<int, long> _total;

    [ObservableProperty]
    private string _toolTip = Strings.AppTitle;

    [ObservableProperty]
    private SpeedPreset _selectedPreset;

    private readonly SpeedLimitState _limit;

    public TrayIconViewModel(IJobService jobs, IAppLifetime app, Action<Action>? post = null, TimeProvider? timeProvider = null, SpeedLimitState? limit = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(app);
        _jobs = jobs;
        _app = app;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        SpeedPresets = BuildPresets();
        _limit = limit ?? new SpeedLimitState(jobs);
        _limit.Changed += OnLimitChanged;
        _selectedPreset = SpeedPresets.FirstOrDefault(p => p.BytesPerSecond == jobs.SpeedLimit) ?? SpeedPresets[0];
        _total = new UiBatchDispatcher<int, long>(totals => _post(() => ShowTotal(totals.Last())), TimeSpan.FromSeconds(1), timeProvider);
    }

    public IReadOnlyList<SpeedPreset> SpeedPresets { get; }

    /// <summary>Unlimited, 1, 5, 10 and 50 MB/s: the same choices in the tray and on the toolbar.</summary>
    public static IReadOnlyList<SpeedPreset> BuildPresets() =>
    [
        new(Strings.TrayUnlimited, null),
        .. new long[] { 1, 5, 10, 50 }.Select(mb => new SpeedPreset(string.Format(CultureInfo.CurrentCulture, _presetFormat, mb), mb * 1024 * 1024)),
    ];

    /// <summary>Follows the engine's progress for the tooltip. Call once, on the UI thread.</summary>
    public void Start()
    {
        _jobs.EventRaised += OnEvent;
        _total.Start();
    }

    public async ValueTask DisposeAsync()
    {
        _jobs.EventRaised -= OnEvent;
        _limit.Changed -= OnLimitChanged;
        await _total.DisposeAsync().ConfigureAwait(false);
    }

    public string OpenLabel => Strings.TrayOpen;

    public string PauseAllLabel => Strings.TrayPauseAll;

    public string ResumeAllLabel => Strings.TrayResumeAll;

    public string SpeedLimitLabel => Strings.TraySpeedLimit;

    public string QuitLabel => Strings.TrayQuit;

    [RelayCommand]
    private void Open() => _app.ShowMainWindow();

    [RelayCommand]
    private Task PauseAllAsync() => _jobs.PauseAllAsync();

    [RelayCommand]
    private Task ResumeAllAsync() => _jobs.ResumeAllAsync();

    [RelayCommand]
    private void SetSpeedLimit(SpeedPreset? preset)
    {
        if (preset is null)
        {
            return;
        }

        _limit.Set(preset.BytesPerSecond);
    }

    [RelayCommand]
    private void Quit() => _app.Quit();

    private void OnLimitChanged(long? bytesPerSecond) => SelectedPreset = SpeedPresets.FirstOrDefault(p => p.BytesPerSecond == bytesPerSecond) ?? SpeedPresets[0];

    private void OnEvent(EngineEvent engineEvent)
    {
        long total;
        lock (_gate)
        {
            switch (engineEvent)
            {
                case JobProgressed progressed:
                    _speeds[progressed.Id] = progressed.BytesPerSecond;
                    break;
                case JobStateChanged { To: not JobState.Downloading } changed:
                    _speeds.Remove(changed.Id);
                    break;
                case JobRemoved removed:
                    _speeds.Remove(removed.Id);
                    break;
                default:
                    return;
            }

            total = _speeds.Values.Sum();
        }

        _total.Post(0, total);
    }

    private void ShowTotal(long bytesPerSecond) =>
        ToolTip = bytesPerSecond <= 0 ? Strings.AppTitle : string.Format(CultureInfo.CurrentCulture, _toolTipFormat, ByteText.Format(bytesPerSecond));
}
