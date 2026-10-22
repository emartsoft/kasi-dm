using Kasi.Ipc.Messages;

namespace Kasi.App.Browser;

/// <summary>
/// Opens the Settings window for <c>openSettings</c>. The pipe is open before the UI exists, so a request that arrives first waits for
/// <see cref="Attach"/> (only the latest section matters: a window is shown once).
/// </summary>
internal sealed class SettingsLauncher : ISettingsLauncher
{
    private readonly Lock _gate = new();
    private Action<SettingsSection>? _show;
    private SettingsSection? _waiting;

    public void Show(SettingsSection section)
    {
        Action<SettingsSection>? show;
        lock (_gate)
        {
            show = _show;
            if (show is null)
            {
                _waiting = section;
            }
        }

        show?.Invoke(section);
    }

    /// <summary>The UI is up; <paramref name="show"/> runs on the UI thread.</summary>
    public void Attach(Action<SettingsSection> show)
    {
        SettingsSection? waiting;
        lock (_gate)
        {
            _show = show;
            waiting = _waiting;
            _waiting = null;
        }

        if (waiting is { } section)
        {
            show(section);
        }
    }
}
