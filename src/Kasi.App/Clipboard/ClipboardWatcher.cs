using Kasi.App.Browser;

namespace Kasi.App.Clipboard;

/// <summary>Where copied text comes from. The real one listens to Windows; tests use a fake.</summary>
internal interface IClipboardSource
{
    /// <summary>Raised, on any thread, after the clipboard content changed. Raised only between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    event Action? Changed;

    void Start();

    void Stop();

    /// <summary>The text on the clipboard now, or null when it holds none.</summary>
    string? ReadText();
}

/// <summary>
/// Clipboard capture (KASI-BRW-007, off by default). While the setting is on, a copied download link opens the Add dialog; while it is
/// off the clipboard is neither listened to nor read. The text is never logged, stored or kept beyond the last accepted address, which
/// stops one copy from opening the dialog twice.
/// </summary>
internal sealed class ClipboardWatcher : IDisposable
{
    private readonly IBrowserSettings _settings;
    private readonly IClipboardSource _source;
    private readonly Action<string> _offer;
    private readonly Lock _gate = new();
    private string? _last;
    private bool _listening;

    public ClipboardWatcher(IBrowserSettings settings, IClipboardSource source, Action<string> offer)
    {
        _settings = settings;
        _source = source;
        _offer = offer;
    }

    /// <summary>Applies the current setting and follows changes to it.</summary>
    public void Start()
    {
        _settings.Changed += OnSettingsChanged;
        _source.Changed += OnClipboardChanged;
        Apply(_settings.Current);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _source.Changed -= OnClipboardChanged;
        Apply(new BrowserSettings { ClipboardCapture = false });
    }

    private void OnSettingsChanged(BrowserSettings settings) => Apply(settings);

    private void Apply(BrowserSettings settings)
    {
        lock (_gate)
        {
            if (settings.ClipboardCapture == _listening)
            {
                return;
            }

            _listening = settings.ClipboardCapture;
            _last = null;
            if (_listening)
            {
                _source.Start();
            }
            else
            {
                _source.Stop();
            }
        }
    }

    private void OnClipboardChanged()
    {
        var current = _settings.Current;
        if (!current.ClipboardCapture)
        {
            return; // a late event after the switch went off: the clipboard is not read
        }

        var link = ClipboardLink.TryParse(_source.ReadText(), current.Extensions);
        if (link is null)
        {
            return;
        }

        lock (_gate)
        {
            if (string.Equals(_last, link, StringComparison.Ordinal))
            {
                return;
            }

            _last = link;
        }

        _offer(link);
    }
}
