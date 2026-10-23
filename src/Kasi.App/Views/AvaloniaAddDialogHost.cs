using Avalonia.Controls;
using Avalonia.Threading;
using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.Engine;

namespace Kasi.App.Views;

/// <summary>Shows the Add download window and the duplicate prompt as real windows. Everything runs on the UI thread; callers may be on any.</summary>
internal sealed class AvaloniaAddDialogHost : IAddDialogHost
{
    private Window? _addWindow;

    public Task ShowAddAsync(AddDownloadViewModel dialog) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var window = new AddDownloadWindow { DataContext = dialog };
        _addWindow = window;
        var closed = new TaskCompletionSource();
        void Close() => window.Close();
        dialog.Closed += Close;
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Activate();
        await closed.Task.ConfigureAwait(true);
        dialog.Closed -= Close;
    });

    public Task ShowPickerAsync(LinkPickerViewModel picker) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var window = new LinkPickerWindow { DataContext = picker };
        var closed = new TaskCompletionSource();
        void Close() => window.Close();
        picker.Closed += Close;
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        window.Activate();
        await closed.Task.ConfigureAwait(true);
        picker.Closed -= Close;
    });

    public Task<DuplicateAction?> AskDuplicateAsync(DuplicateCheckResult check) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var model = new DuplicatePromptViewModel(check);
        var window = new DuplicatePromptWindow { DataContext = model };
        var closed = new TaskCompletionSource();
        model.Closed += window.Close;
        window.Closed += (_, _) => closed.TrySetResult();
        // modal over the Add window when there is one, so the user cannot edit the fields while deciding
        if (_addWindow is { IsVisible: true } parent)
        {
            _ = window.ShowDialog(parent);
        }
        else
        {
            window.Show();
        }

        await closed.Task.ConfigureAwait(true);
        return model.Chosen;
    });
}
