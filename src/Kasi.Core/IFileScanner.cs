namespace Kasi.Core;

/// <summary>What a scan found.</summary>
public enum ScanVerdict
{
    /// <summary>The scan could not run (no scanner here, the file is too big, a provider error); the download is not held up.</summary>
    NotScanned,

    /// <summary>Nothing was found.</summary>
    Clean,

    /// <summary>The antivirus flagged the file.</summary>
    Detected,
}

/// <summary>
/// The result of a scan. <see cref="Note"/> is a fixed phrase for the job log ("file too large to scan"): never an address, a path or the name
/// of what was found (KASI-CAT-004; CLAUDE.md rule 3).
/// </summary>
public sealed record ScanResult(ScanVerdict Verdict, string? Note = null)
{
    public static ScanResult Clean { get; } = new(ScanVerdict.Clean);

    public static ScanResult Detected { get; } = new(ScanVerdict.Detected);

    public static ScanResult NotScanned(string note) => new(ScanVerdict.NotScanned, note);
}

/// <summary>
/// Scans a finished download before it gets its final name (KASI-CAT-004, M2.11e). Off unless the user turned the setting on. A scan that
/// cannot run is <see cref="ScanVerdict.NotScanned"/>, never a failed download; only <see cref="ScanVerdict.Detected"/> ends the job, and
/// Kasi never deletes the file: the antivirus has already dealt with it.
/// </summary>
public interface IFileScanner
{
    /// <summary>False while the setting is off: the engine then does not call <see cref="ScanAsync"/> and logs nothing.</summary>
    bool IsEnabled { get; }

    Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken);
}
