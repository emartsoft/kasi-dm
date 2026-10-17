namespace Kasi.Engine;

/// <summary>
/// Classification of a failed probe attempt so the engine can decide whether to retry, ask for credentials or a
/// refreshed link, or fail the job permanently (TRD 6.1, TRD 6.6, KASI-NFR-011).
/// </summary>
public enum ProbeErrorKind
{
    AuthRequired,
    PermanentError,
    Throttled,
    TransientError,
    TooManyRedirects,
    InvalidRedirect,
    ProtocolError,
    CertificateError,
}
