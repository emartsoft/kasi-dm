namespace Kasi.Core;

/// <summary>The text stored in <c>job.kind</c> (lower case, as the CHECK constraint requires).</summary>
public static class JobKindText
{
    public static string ToText(JobKind kind) => kind switch
    {
        JobKind.Http => "http",
        JobKind.Ftp => "ftp",
        JobKind.Media => "media",
        JobKind.Torrent => "torrent",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a defined job kind."),
    };

    /// <summary>Exact, case-sensitive match; numbers and unknown text are rejected.</summary>
    public static bool TryParse(string? text, out JobKind kind)
    {
        (var found, kind) = text switch
        {
            "http" => (true, JobKind.Http),
            "ftp" => (true, JobKind.Ftp),
            "media" => (true, JobKind.Media),
            "torrent" => (true, JobKind.Torrent),
            _ => (false, default(JobKind)),
        };
        return found;
    }
}
