using Kasi.Core.Logging;
using Kasi.Engine.Interop;

namespace Kasi.Engine;

/// <summary>
/// Outcome of a disk or part-file operation (TRD 6.4, ADR 0005). Carries no user-facing text: the application maps
/// <see cref="ErrorKind"/> to localised resource strings, while <see cref="Detail"/> holds a redacted diagnostic note for logs.
/// </summary>
public readonly record struct DiskStatus
{
    private DiskStatus(bool isSuccess, DiskErrorKind? errorKind, int? nativeErrorCode, string? detail)
    {
        IsSuccess = isSuccess;
        ErrorKind = errorKind;
        NativeErrorCode = nativeErrorCode;
        Detail = detail;
    }

    public static DiskStatus Success => new(isSuccess: true, errorKind: null, nativeErrorCode: null, detail: null);

    public bool IsSuccess { get; }

    public DiskErrorKind? ErrorKind { get; }

    public int? NativeErrorCode { get; }

    /// <summary>Redacted diagnostic detail for logs only; never contains raw directory paths or user-facing messages.</summary>
    public string? Detail { get; }

    public static DiskStatus Failure(DiskErrorKind errorKind, string? detail = null, int? nativeErrorCode = null)
    {
        if (!Enum.IsDefined(errorKind))
        {
            throw new ArgumentOutOfRangeException(nameof(errorKind), errorKind, "Unknown disk error kind.");
        }

        return new DiskStatus(isSuccess: false, errorKind, nativeErrorCode, detail);
    }

    /// <summary>
    /// Classifies an <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> into a <see cref="DiskStatus"/>,
    /// redacting <paramref name="path"/> so raw filesystem paths never appear in <see cref="Detail"/>.
    /// </summary>
    public static DiskStatus FromException(Exception exception, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var nativeCode = Kernel32.TryGetWin32ErrorCode(exception.HResult);
        var kind = Kernel32.IsOutOfSpaceHResult(exception.HResult)
            ? DiskErrorKind.NotEnoughSpace
            : DiskErrorKind.IoError;

        var redactedTarget = string.IsNullOrWhiteSpace(path) ? Redact.Placeholder : Redact.Path(path);
        var codeText = nativeCode is { } win32 ? $"win32={win32}" : $"hresult=0x{exception.HResult:X8}";
        return Failure(kind, $"{exception.GetType().Name} ({codeText}) on '{redactedTarget}'.", nativeCode);
    }
}
