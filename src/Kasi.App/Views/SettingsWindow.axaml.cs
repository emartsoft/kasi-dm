using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

internal sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CancelButton.Click += OnCancel;
        HitList.SelectionChanged += OnHitChosen;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    // Ctrl+E: "Find a setting"
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e is { Key: Key.E, KeyModifiers: KeyModifiers.Control })
        {
            FindBox.Focus();
            FindBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnHitChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (HitList.SelectedItem is SettingHit hit && DataContext is SettingsViewModel model)
        {
            model.OpenHitCommand.Execute(hit);
            HitList.SelectedItem = null;
        }
    }
}
