using Kasi.App.ViewModels;
using Kasi.Ipc.Messages;
using Kasi.Media;

namespace Kasi.App.Browser;

/// <summary>
/// The streams detected in each tab (KASI-MED-001). Bounded on both axes, so a page or a long session cannot grow the
/// process without limit: the oldest stream of a tab goes first, and when too many tabs are known the least recently touched
/// tab goes.
/// </summary>
internal sealed class BrowserMediaCatalog
{
    public const int MaxPerTab = 64;
    public const int MaxTabs = 256;

    private readonly object _gate = new();
    private readonly Dictionary<int, Tab> _tabs = [];
    private long _tick;

    public void Add(MediaDetected detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        lock (_gate)
        {
            if (!_tabs.TryGetValue(detected.TabId, out var tab))
            {
                if (_tabs.Count >= MaxTabs)
                {
                    _tabs.Remove(_tabs.MinBy(entry => entry.Value.Touched).Key);
                }

                tab = new Tab();
                _tabs[detected.TabId] = tab;
            }

            tab.Touched = ++_tick;
            if (tab.Streams.TryAdd(detected.Url, detected))
            {
                tab.Order.Enqueue(detected.Url);
                while (tab.Streams.Count > MaxPerTab && tab.Order.TryDequeue(out var oldest))
                {
                    tab.Streams.Remove(oldest);
                }
            }
            else
            {
                tab.Streams[detected.Url] = detected;
            }
        }
    }

    /// <summary>The tab's streams, oldest first.</summary>
    public IReadOnlyList<MediaDetected> ForTab(int tabId)
    {
        lock (_gate)
        {
            return _tabs.TryGetValue(tabId, out var tab) ? [.. tab.Order.Select(url => tab.Streams[url])] : [];
        }
    }

    public IReadOnlyList<MediaDetected> All()
    {
        lock (_gate)
        {
            return [.. _tabs.Values.SelectMany(tab => tab.Order.Select(url => tab.Streams[url]))];
        }
    }

    public void RemoveTab(int tabId)
    {
        lock (_gate)
        {
            _tabs.Remove(tabId);
        }
    }

    private sealed class Tab
    {
        public long Touched { get; set; }

        public Dictionary<string, MediaDetected> Streams { get; } = new(StringComparer.Ordinal);

        public Queue<string> Order { get; } = new();
    }
}

internal enum MediaCaptureStatus
{
    Accepted,
    Protected,
    Cancelled,
    Failed,
}

internal sealed record MediaCaptureResult(MediaCaptureStatus Status, string? Detail = null);

/// <summary>Bound after the host and UI exist: loads the manifest, refuses DRM or creates the default media job (M3.5).</summary>
internal interface IBrowserMediaCapture
{
    Task<MediaCaptureResult> CaptureAsync(CaptureMedia message, CancellationToken cancellationToken);
}

/// <summary>The media side of the browser services; the capture implementation is attached once the DI host is ready.</summary>
internal sealed class BrowserMediaServices
{
    public BrowserMediaCatalog Catalog { get; } = new();

    public IBrowserMediaCapture? Capture { get; set; }

    public IMediaPicker? Picker { get; set; }

    /// <summary>How long a <c>captureMedia</c> may keep the browser waiting for its answer; a slower capture finishes in the background.</summary>
    public TimeSpan AckBudget { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Shows the picker and returns the selected quality; null means the user cancelled.</summary>
internal interface IMediaPicker
{
    Task<MediaPickerSelection?> PickAsync(MediaPresentation presentation, string title, string? thumbnailUrl, CancellationToken cancellationToken);
}
