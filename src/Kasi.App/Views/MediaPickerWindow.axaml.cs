using Avalonia.Controls;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

/// <summary>The media picker window (KASI-MED-002): pure view model, keyboard-default Download, cancel by Esc.</summary>
internal sealed partial class MediaPickerWindow : Window
{
    public MediaPickerWindow(MediaPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Closed += Close;
        Opened += (_, _) => _ = viewModel.LoadThumbnailAsync();
        Closed += (_, _) => viewModel.Closed -= Close;
    }

    /// <summary>For the Avalonia previewer; the real constructor takes the view model.</summary>
    public MediaPickerWindow()
    {
        InitializeComponent();
    }
}
