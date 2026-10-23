using Kasi.Ipc;
using Kasi.Ipc.Messages;

namespace Kasi.App.Browser;

/// <summary>
/// What Kasi tells the browser extension to take (TRD 9.2): the source of the <c>rules</c> message. An empty list intercepts nothing, so
/// the defaults are populated. <see cref="Normalized"/> is the only way a value reaches the pipe or the disk: every list is held to the
/// protocol's caps, so a hand-edited file or a settings window can never produce a <c>rules</c> message the extension would refuse.
/// </summary>
internal sealed record BrowserSettings
{
    public static IReadOnlyList<string> DefaultExtensions { get; } =
    [
        "zip", "rar", "7z", "tar", "gz", "tar.gz", "bz2", "xz", "iso", "img", "exe", "msi", "msix", "dmg", "pkg", "deb", "rpm", "apk",
        "mp4", "mkv", "avi", "mov", "webm", "mp3", "flac", "wav",
    ];

    public static IReadOnlyList<string> DefaultMimeTypes { get; } =
    [
        "application/zip", "application/x-rar-compressed", "application/vnd.rar", "application/x-7z-compressed", "application/gzip",
        "application/x-gzip", "application/x-tar", "application/x-iso9660-image", "application/x-msdownload",
        "application/vnd.microsoft.portable-executable", "application/x-msi", "application/octet-stream", "video/*", "audio/*",
    ];

    public bool Enabled { get; init; } = true;

    /// <summary>Open the Add download window for a download the browser handed over; off adds it at once with the defaults.</summary>
    public bool ShowAddDialog { get; init; } = true;

    public IReadOnlyList<string> Extensions { get; init; } = DefaultExtensions;

    public IReadOnlyList<string> MimeTypes { get; init; } = DefaultMimeTypes;

    public IReadOnlyList<string> ExcludedSites { get; init; } = [];

    public BypassKey BypassKey { get; init; } = BypassKey.Alt;

    public int AckTimeoutMs { get; init; } = 2000;

    /// <summary>Downloads smaller than this are left to the browser; null means no minimum.</summary>
    public long? MinSizeBytes { get; init; }

    /// <summary>Lowercase, trimmed, no leading dots on extensions, no blanks or duplicates, entries and counts within the protocol caps.</summary>
    public BrowserSettings Normalized() => this with
    {
        Extensions = CleanList(Extensions, trimDots: true),
        MimeTypes = CleanList(MimeTypes, trimDots: false),
        ExcludedSites = CleanList(ExcludedSites, trimDots: true),
        AckTimeoutMs = Math.Clamp(AckTimeoutMs, IpcLimits.AckTimeoutMinMs, IpcLimits.AckTimeoutMaxMs),
        MinSizeBytes = MinSizeBytes is { } size ? Math.Clamp(size, 0, IpcLimits.MinSizeMaxBytes) : null,
    };

    private static List<string> CleanList(IEnumerable<string>? values, bool trimDots)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<string>();
        foreach (var raw in values ?? [])
        {
            var value = raw.Trim().ToLowerInvariant();
            if (trimDots)
            {
                value = value.TrimStart('.');
            }

            if (value.Length is 0 or > IpcLimits.ListEntryLength || value.Any(char.IsControl) || !seen.Add(value))
            {
                continue;
            }

            cleaned.Add(value);
            if (cleaned.Count == IpcLimits.ListEntries)
            {
                break;
            }
        }

        return cleaned;
    }
}
