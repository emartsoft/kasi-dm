using Avalonia.Controls;
using Avalonia.Input;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Grid.SelectionChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                model.SetSelection(Grid.SelectedItems.OfType<JobRowViewModel>());
            }
        };
        Grid.DoubleTapped += (_, _) =>
        {
            if (DataContext is MainWindowViewModel { SelectedRow: { } row } model)
            {
                model.OpenRowCommand.Execute(row);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                model.SearchRequested += () => SearchBox.Focus(NavigationMethod.Unspecified);
            }
        };
    }
}
