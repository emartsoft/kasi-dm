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
    private readonly SpeedHistory _history = new();

    [ObservableProperty]
    private string _totalSpeedText = ByteText.Format(0) + "/s";

    [ObservableProperty]
    private IReadOnlyList<double> _speedSamples = [];

    public TrayIconViewModel(IJobService jobs, IAppLifetime app, Action<Action>? post = null, TimeProvider? timeProvider = null, SpeedLimitState? limit = null, IReadOnlyList<SpeedProfile>? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(app);
        _jobs = jobs;
        _app = app;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        SpeedPresets = BuildPresets(profiles);
        _limit = limit ?? new SpeedLimitState(jobs);
        _limit.Changed += OnLimitChanged;
        _selectedPreset = SpeedPresets.FirstOrDefault(p => p.BytesPerSecond == jobs.SpeedLimit) ?? SpeedPresets[0];
        _total = new UiBatchDispatcher<int, long>(totals => _post(() => ShowTotal(totals.Last())), TimeSpan.FromSeconds(1), timeProvider);
    }

    public IReadOnlyList<SpeedPreset> SpeedPresets { get; }

    /// <summary>Unlimited, 1, 5, 10 and 50 MB/s plus the user's named profiles.</summary>
    public static IReadOnlyList<SpeedPreset> BuildPresets(IReadOnlyList<SpeedProfile>? profiles = null)
    {
        var presets = new List<SpeedPreset>
        {
            new(Strings.TrayUnlimited, null),
        };
        presets.AddRange(new long[] { 1, 5, 10, 50 }.Select(mb => new SpeedPreset(string.Format(CultureInfo.CurrentCulture, _presetFormat, mb), mb * 1024 * 1024)));
        if (profiles is not null)
        {
            foreach (var profile in profiles)
            {
                if (presets.All(preset => preset.BytesPerSecond != profile.LimitBps))
                {
                    presets.Add(new SpeedPreset(profile.Name, profile.LimitBps));
                }
            }
        }

        return presets;
    }

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

    public string FlyoutName => Strings.TrayFlyoutName;

    public string FlyoutSpeedLabel => Strings.TrayFlyoutSpeed;

    public string FlyoutLimitLabel => Strings.TrayFlyoutLimit;

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

    private void ShowTotal(long bytesPerSecond)
    {
        ToolTip = bytesPerSecond <= 0 ? Strings.AppTitle : string.Format(CultureInfo.CurrentCulture, _toolTipFormat, ByteText.Format(bytesPerSecond));
        _history.Add(bytesPerSecond);
        SpeedSamples = _history.Snapshot();
        TotalSpeedText = ByteText.Format(Math.Max(0, bytesPerSecond)) + "/s";
    }
}
