using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// What every finished download goes through before it gets its final name, whatever made it (HTTP, FTP, a media mux): the optional antivirus
/// scan (KASI-CAT-004), then the Mark-of-the-Web (KASI-CAT-003). One step, so no kind of job can skip it. The log lines are fixed phrases.
/// </summary>
public static class FileSafety
{
    public const string FlaggedMessage = "Flagged by antivirus. Kasi leaves the file where the antivirus put it.";

    /// <summary>
    /// Scans <paramref name="path"/> when <paramref name="scanner"/> is on and marks it. Returns true when the antivirus flagged the file:
    /// nothing is marked then, and the file is left alone for the caller to fail the job. A scan that cannot run, or a volume that cannot
    /// hold the mark, never fails the download.
    /// </summary>
    /// <param name="source">Where the file came from, for the mark's <c>HostUrl</c>; null or a non-HTTP address leaves it out.</param>
    /// <param name="markWriter">Replaces <see cref="MarkOfTheWeb.TryWrite"/>; tests only.</param>
    public static async Task<bool> ApplyAsync(
        string path,
        Uri? source,
        IFileScanner? scanner,
        Action<string>? log,
        CancellationToken cancellationToken,
        Func<string, Uri?, bool>? markWriter = null)
    {
        if (scanner is { IsEnabled: true })
        {
            var scan = await ScanQuietlyAsync(scanner, path, cancellationToken).ConfigureAwait(false);
            switch (scan.Verdict)
            {
                case ScanVerdict.Detected:
                    log?.Invoke("Scan: flagged by antivirus");
                    return true;
                case ScanVerdict.Clean:
                    log?.Invoke("Scan: clean");
                    break;
                default:
                    log?.Invoke("Scan: not scanned" + (scan.Note is { } note ? " (" + note + ")" : string.Empty));
                    break;
            }
        }

        if (!(markWriter ?? MarkOfTheWeb.TryWrite)(path, source))
        {
            log?.Invoke("Mark-of-the-Web not supported on this volume");
        }

        return false;
    }

    // a scanner that fails for any reason other than being cancelled has not scanned: it never fails the download
    private static async Task<ScanResult> ScanQuietlyAsync(IFileScanner scanner, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await scanner.ScanAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return ScanResult.NotScanned("the scan failed");
        }
    }
}
