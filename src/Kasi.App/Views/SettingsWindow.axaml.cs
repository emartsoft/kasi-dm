using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Kasi.App.Views;

internal sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CancelButton.Click += OnCancel;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
