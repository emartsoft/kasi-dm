using Avalonia.Controls;
using Avalonia.Interactivity;
using Kasi.App.ViewModels;
using Kasi.Engine;

namespace Kasi.App.Views;

internal sealed partial class DuplicatePromptWindow : Window
{
    public DuplicatePromptWindow() => InitializeComponent();

    private void OnChoose(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DuplicateAction action } && DataContext is DuplicatePromptViewModel model)
        {
            model.Choose(action);
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DuplicatePromptViewModel model)
        {
            model.Cancel();
        }
    }
}
