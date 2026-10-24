using System.Collections.Concurrent;
using Kasi.Ipc.Messages;

namespace Kasi.App.Browser;

/// <summary>The streams detected in each tab, bounded so a page cannot grow the process without limit (KASI-MED-001).</summary>
internal sealed class BrowserMediaCatalog
{
    public const int MaxPerTab = 64;

    private readonly ConcurrentDictionary<int, ConcurrentDictionary<string, MediaDetected>> _tabs = new();

    public void Add(MediaDetected detected)
    {
        ArgumentNullException.ThrowIfNull(detected);
        var tab = _tabs.GetOrAdd(detected.TabId, _ => new ConcurrentDictionary<string, MediaDetected>(StringComparer.Ordinal));
        tab[detected.Url] = detected;
        while (tab.Count > MaxPerTab)
        {
            var oldest = tab.Keys.FirstOrDefault();
            if (oldest is null || !tab.TryRemove(oldest, out _))
            {
                break;
            }
        }
    }

    public IReadOnlyList<MediaDetected> ForTab(int tabId) =>
        _tabs.TryGetValue(tabId, out var tab) ? [.. tab.Values] : [];

    public IReadOnlyList<MediaDetected> All() => [.. _tabs.Values.SelectMany(tab => tab.Values)];

    public void RemoveTab(int tabId) => _tabs.TryRemove(tabId, out _);
}

internal enum MediaCaptureStatus
{
    Accepted,
    Protected,
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
}
