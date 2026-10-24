using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

internal sealed partial class DetailsWindow : Window
{
    public DetailsWindow() => InitializeComponent();

    // the address without its query, as the window shows it: a query can carry a token
    private void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DetailsViewModel model && Clipboard is { } clipboard)
        {
            _ = clipboard.SetTextAsync(model.Address);
        }
    }
}
