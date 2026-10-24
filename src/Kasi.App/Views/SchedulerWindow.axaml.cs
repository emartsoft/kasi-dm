using Avalonia.Controls;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

internal partial class SchedulerWindow : Window
{
    public SchedulerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SchedulerViewModel vm)
            {
                vm.RequestClose += Close;
            }
        };
    }
}
