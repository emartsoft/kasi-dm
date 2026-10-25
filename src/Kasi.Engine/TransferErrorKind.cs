namespace Kasi.Engine;

/// <summary>The reason <see cref="HttpTransferRunner"/> could not complete a transfer (M1.7b, M1.8).</summary>
public enum TransferErrorKind
{
    /// <summary>The probe failed; <see cref="TransferFailed.ProbeFailure"/> carries the classified failure.</summary>
    ProbeFailed,

    /// <summary>A certificate failed validation; no bypass exists (KASI-NFR-011).</summary>
    CertificateError,

    /// <summary>The target disk is full; the part file and flushed cursors are preserved (KASI-ENG-008).</summary>
    DiskNotEnoughSpace,

    /// <summary>The final file already exists and overwriting was not requested.</summary>
    DiskTargetExists,

    /// <summary>A part-file or write operation failed for a reason other than no space.</summary>
    DiskError,

    /// <summary>The sealed part file did not match the supplied checksum; it is kept for inspection.</summary>
    VerificationFailed,

    /// <summary>The antivirus scan flagged the finished file (M2.11e); it is left where the antivirus put it, never deleted by Kasi.</summary>
    FlaggedByAntivirus,

    /// <summary>The server returned an HTTP status the transfer loop cannot use.</summary>
    HttpStatusError,

    /// <summary>The server violated the range protocol or truncated a response.</summary>
    ProtocolError,

    /// <summary>A transient network failure; M1.8 adds retry on top of this outcome.</summary>
    TransientNetworkError,

    /// <summary>The remote file changed; a resume must restart rather than splice bytes (KASI-RES-003).</summary>
    FileChanged,

    /// <summary>The caller cancelled the transfer; any flushed progress is reported for a later resume.</summary>
    Cancelled,

    /// <summary>The machine is offline; the engine facade pauses all jobs and resumes them on reconnect.</summary>
    Offline,

    /// <summary>The persisted segment structure or cursors could not be written (M1.9).</summary>
    CheckpointError,
}
