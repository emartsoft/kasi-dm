using Kasi.Core;

namespace Kasi.App.Engine;

/// <summary>The scanner the engine is given: it scans through <paramref name="inner"/> only while the "scan downloads" setting is on, read at each completion.</summary>
internal sealed class SettingGatedScanner(IUiSettings settings, IFileScanner inner) : IFileScanner
{
    public bool IsEnabled => settings.Current.ScanDownloads && inner.IsEnabled;

    public Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken) => inner.ScanAsync(path, cancellationToken);
}
