using System.Runtime.InteropServices;
using CommunityToolkit.WinUI.Notifications;
using Kasi.App.Resources;
using Kasi.Core;

namespace Kasi.App.Notifications;

/// <summary>
/// The real toasts, through CommunityToolkit.WinUI.Notifications. An unpackaged app has no installer shortcut yet (M4), so this is checked
/// by hand (docs/qa), not by a test; the router and the notifier it feeds are tested. A machine that cannot show toasts is not an error.
/// </summary>
internal sealed class WindowsToastService : INotificationService
{
    public void ShowCompleted(JobId id, string fileName, string? details = null) => Show(id, Strings.ToastCompleted, fileName, details, withButtons: true);

    public void ShowFailed(JobId id, string fileName) => Show(id, Strings.ToastFailed, fileName, null, withButtons: false);

    /// <summary>Routes a clicked toast to <paramref name="router"/>. Dispose to stop listening.</summary>
    public static IDisposable Activate(NotificationRouter router)
    {
        void OnActivated(ToastNotificationActivatedEventArgsCompat args)
        {
            var parsed = ToastArguments.Parse(args.Argument);
            var joined = string.Join('&', parsed.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
            _ = router.HandleAsync(joined);
        }

        ToastNotificationManagerCompat.OnActivated += OnActivated;
        return new Subscription(() => ToastNotificationManagerCompat.OnActivated -= OnActivated);
    }

    private static void Show(JobId id, string title, string fileName, string? details, bool withButtons)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument(ToastActions.Action, ToastActions.OpenFile)
                .AddArgument(ToastActions.Job, id.ToString())
                .AddText(title)
                .AddText(fileName);
            if (details is not null)
            {
                builder.AddText(details);
            }

            if (LogoPath() is { } logo)
            {
                builder.AddAppLogoOverride(new Uri(logo), ToastGenericAppLogoCrop.Circle);
            }

            if (withButtons)
            {
                builder
                    .AddButton(new ToastButton().SetContent(Strings.ToastOpenFile).AddArgument(ToastActions.Action, ToastActions.OpenFile).AddArgument(ToastActions.Job, id.ToString()))
                    .AddButton(new ToastButton().SetContent(Strings.ToastOpenFolder).AddArgument(ToastActions.Action, ToastActions.OpenFolder).AddArgument(ToastActions.Job, id.ToString()));
            }

            builder.Show();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or PlatformNotSupportedException)
        {
            // toasts are off or not available here: the download is done either way
        }
    }

    // the Kasi mark as the toast's logo: a toast needs a file, so the embedded picture is written once beside the settings
    private static string? LogoPath()
    {
        try
        {
            var path = Path.Combine(Kasi.App.Engine.AppPaths.DataFolder(), "toast-logo.png");
            if (!File.Exists(path))
            {
                using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Kasi.App/Assets/kasi.png"));
                using var target = File.Create(path);
                source.CopyTo(target);
            }

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class Subscription(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
