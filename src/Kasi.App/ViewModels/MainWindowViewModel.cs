using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.App.Resources;
using Kasi.App.Startup;

namespace Kasi.App.ViewModels;

internal sealed partial class MainWindowViewModel : ObservableObject, IAddDownloadService
{
    public string Title => Strings.AppTitle;

    public string AutomationName => Strings.MainWindowAutomationName;

    /// <summary>URLs asked for by a launch, waiting for the Add dialog (PR 2.8d).</summary>
    public ObservableCollection<string> PendingAddUrls { get; } = [];

    public void RequestAdd(IReadOnlyList<string> urls)
    {
        foreach (var url in urls)
        {
            PendingAddUrls.Add(url);
        }
    }
}
