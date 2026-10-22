using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.App.Browser;
using Kasi.App.Resources;
using Kasi.App.Startup;

namespace Kasi.App.ViewModels;

internal sealed partial class MainWindowViewModel : ObservableObject, IAddDownloadService, IBrowserRequestSink
{
    public string Title => Strings.AppTitle;

    public string AutomationName => Strings.MainWindowAutomationName;

    /// <summary>URLs asked for by a launch, waiting for the Add dialog (PR 2.8d).</summary>
    public ObservableCollection<string> PendingAddUrls { get; } = [];

    /// <summary>Downloads the browser handed over, waiting for the Add dialog (PR 2.8d). Holds cookie values: never printed or logged.</summary>
    public ObservableCollection<BrowserDownload> PendingBrowserDownloads { get; } = [];

    /// <summary>Link batches waiting for the link picker (PR 2.8d).</summary>
    public ObservableCollection<BrowserBatch> PendingBrowserBatches { get; } = [];

    public void ShowAdd(BrowserDownload download) => PendingBrowserDownloads.Add(download);

    public void ShowPicker(BrowserBatch batch) => PendingBrowserBatches.Add(batch);

    public void RequestAdd(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            PendingAddUrls.Add(url);
        }
    }
}
