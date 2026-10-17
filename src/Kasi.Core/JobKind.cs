namespace Kasi.Core;

/// <summary>What a job downloads; matches the <c>job.kind</c> CHECK constraint in TRD 11.2.</summary>
public enum JobKind
{
    Http,
    Ftp,
    Media,
    Torrent,
}
