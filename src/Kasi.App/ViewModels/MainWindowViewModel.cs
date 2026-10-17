using CommunityToolkit.Mvvm.ComponentModel;
using Kasi.App.Resources;

namespace Kasi.App.ViewModels;

internal sealed partial class MainWindowViewModel : ObservableObject
{
    public string Title => Strings.AppTitle;

    public string AutomationName => Strings.MainWindowAutomationName;
}
