using Avalonia.Controls;
using Avalonia.Threading;
using Kasi.App.Browser;
using Kasi.App.ViewModels;
using Kasi.Media;

namespace Kasi.App.Views;

/// <summary>Shows <see cref="MediaPickerWindow"/> on the UI thread and returns its selection to the capture service.</summary>
internal sealed class AvaloniaMediaPicker : IMediaPicker
{
    /// <summary>Set after the main window exists; null is allowed for a headless/test host.</summary>
    public Func<Window?>? Owner { get; set; }

    public async Task<MediaPickerSelection?> PickAsync(
        MediaPresentation presentation,
        string title,
        string? thumbnailUrl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var model = new MediaPickerViewModel(presentation, title, thumbnailUrl);
            var window = new MediaPickerWindow(model);
            var owner = Owner?.Invoke();
            if (owner is not null && owner.IsVisible)
            {
                await window.ShowDialog(owner);
            }
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (_, _) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }

            return model.Result;
        }).ConfigureAwait(false);
    }
}
