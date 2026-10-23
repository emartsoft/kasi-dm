using Avalonia.Controls;
using Avalonia.Threading;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

/// <summary>The delete confirmation as a real window, modal over the main window when it is showing.</summary>
internal sealed class AvaloniaConfirmHost(Func<Window?> owner) : IConfirmHost
{
    public Task<DeleteChoice?> ConfirmDeleteAsync(ConfirmDeleteViewModel dialog) => Dispatcher.UIThread.InvokeAsync(async () =>
    {
        var window = new ConfirmDeleteWindow { DataContext = dialog };
        var closed = new TaskCompletionSource();
        dialog.Closed += window.Close;
        window.Closed += (_, _) => closed.TrySetResult();
        if (owner() is { IsVisible: true } parent)
        {
            _ = window.ShowDialog(parent);
        }
        else
        {
            window.Show();
        }

        await closed.Task.ConfigureAwait(true);
        return dialog.Result;
    });
}
