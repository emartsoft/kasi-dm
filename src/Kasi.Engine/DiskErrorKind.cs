namespace Kasi.Engine;

/// <summary>
/// Classification of a part-file or disk operation failure (TRD 6.4, 6.5, ADR 0005).
/// </summary>
public enum DiskErrorKind
{
    /// <summary>Pre-start free-space check failed, or an OS write, pre-allocation or flush failed with disk full or quota exceeded.</summary>
    NotEnoughSpace,

    /// <summary>On resume, the <c>.kasipart</c> file is missing or its length does not match <c>TotalBytes</c>.</summary>
    PartFileMismatch,

    /// <summary>At seal, the actual <c>.kasipart</c> length differs from the expected length.</summary>
    LengthMismatch,

    /// <summary>At seal, the caller-supplied written-byte total or allocated-range check shows the file is not fully written.</summary>
    IncompleteData,

    /// <summary>At commit, the final destination path already exists and overwriting was not requested.</summary>
    TargetExists,

    /// <summary>Any other I/O or access failure (device removed, access denied, sharing violation).</summary>
    IoError,
}
