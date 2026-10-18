namespace Kasi.Core;

/// <summary>What a probe learned about the remote file and a restart needs to resume it (TRD 6.1, 6.5).</summary>
public sealed record JobTransferMetadata(
    string? FinalUrl,
    string FileName,
    long? TotalBytes,
    string? ETag,
    string? LastModified,
    bool Resumable);
