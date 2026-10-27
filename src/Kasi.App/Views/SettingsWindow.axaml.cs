using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

internal sealed partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        CancelButton.Click += OnCancel;
        HitList.SelectionChanged += OnHitChosen;
        ExportBackupButton.Click += OnExportBackup;
        ImportBackupButton.Click += OnImportBackup;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnExportBackup(object? sender, RoutedEventArgs e) => _ = ExportBackupAsync();

    private void OnImportBackup(object? sender, RoutedEventArgs e) => _ = ImportBackupAsync();

    private async Task ExportBackupAsync()
    {
        if (DataContext is not SettingsViewModel model)
        {
            return;
        }

        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = "kasi-backup.json",
                FileTypeChoices = [new FilePickerFileType("Kasi backup") { Patterns = ["*.json"] }],
            }).ConfigureAwait(true);
            if (file is not null)
            {
                await model.ExportBackupAsync(file.Path.LocalPath).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            model.BackupStatus = Kasi.App.Resources.Strings.SettingsBackupFailed;
        }
    }

    private async Task ImportBackupAsync()
    {
        if (DataContext is not SettingsViewModel model)
        {
            return;
        }

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Kasi backup") { Patterns = ["*.json"] }],
            }).ConfigureAwait(true);
            if (files.Count > 0)
            {
                await model.ImportBackupAsync(files[0].Path.LocalPath).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            model.BackupStatus = Kasi.App.Resources.Strings.SettingsBackupFailed;
        }
    }

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
