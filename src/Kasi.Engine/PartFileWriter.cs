using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Kasi.Core.Logging;
using Kasi.Engine.Interop;
using Microsoft.Win32.SafeHandles;

namespace Kasi.Engine;

/// <summary>
/// Manages the single <c>&lt;fileName&gt;.kasipart</c> file for a download job: sparse pre-allocation, random-access writes,
/// durability flushes (<c>FlushFileBuffers</c>), two-phase finalisation (<see cref="SealAsync"/> and <see cref="CommitAsync"/>),
/// and cancellation cleanup (<see cref="Abandon"/>) (TRD 6.4, 6.5, ADR 0005).
/// </summary>
public sealed class PartFileWriter : IAsyncDisposable, IDisposable
{
    public const string PartExtension = ".kasipart";
    public const int WriteBufferSize = 256 * 1024;

    private readonly SafeFileHandle _handle;
    private bool _disposed;
    private bool _committed;

    private PartFileWriter(
        SafeFileHandle handle,
        string partPath,
        string finalPath,
        long? totalBytes,
        bool isSparse)
    {
        _handle = handle;
        PartPath = partPath;
        FinalPath = finalPath;
        TotalBytes = totalBytes;
        IsSparse = isSparse;
    }

    public string PartPath { get; }

    public string FinalPath { get; }

    public long? TotalBytes { get; }

    public bool IsSparse { get; private set; }

    public bool IsSealed { get; private set; }

    public bool IsCommitted => _committed;

    internal SafeFileHandle Handle => _handle;

    internal Func<long, int, Exception?>? WriteFaultInjector { get; set; }

    internal Func<Exception?>? FlushFaultInjector { get; set; }

    public static PartFileOpenOutcome TryOpen(
        string folderPath,
        string fileName,
        long? totalBytes,
        bool resume = false,
        long alreadyWrittenBytes = 0,
        IDiskSpaceProvider? spaceProvider = null) =>
        TryOpenCore(folderPath, fileName, totalBytes, resume, alreadyWrittenBytes, spaceProvider, setLengthFaultInjector: null);

    internal static PartFileOpenOutcome TryOpenCore(
        string folderPath,
        string fileName,
        long? totalBytes,
        bool resume,
        long alreadyWrittenBytes,
        IDiskSpaceProvider? spaceProvider,
        Func<Exception?>? setLengthFaultInjector)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return new PartFileOpenFailure(DiskStatus.Failure(DiskErrorKind.IoError, "Target folder path is empty."));
        }

        if (!IsValidBareFileName(fileName))
        {
            return new PartFileOpenFailure(DiskStatus.Failure(
                DiskErrorKind.IoError,
                $"Invalid bare file name '{Redact.Path(fileName)}'."));
        }

        if (totalBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes), totalBytes, "TotalBytes cannot be negative.");
        }

        if (alreadyWrittenBytes < 0 || (totalBytes is { } maxWritten && alreadyWrittenBytes > maxWritten))
        {
            throw new ArgumentOutOfRangeException(
                nameof(alreadyWrittenBytes),
                alreadyWrittenBytes,
                "AlreadyWrittenBytes must be within [0, TotalBytes].");
        }

        string fullFolder;
        string partPath;
        string finalPath;
        try
        {
            fullFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            finalPath = Path.GetFullPath(Path.Combine(fullFolder, fileName));
            partPath = Path.GetFullPath(Path.Combine(fullFolder, fileName + PartExtension));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new PartFileOpenFailure(DiskStatus.Failure(DiskErrorKind.IoError, "Target path could not be resolved."));
        }

        if (!IsDirectChildOf(finalPath, fullFolder) || !IsDirectChildOf(partPath, fullFolder))
        {
            return new PartFileOpenFailure(DiskStatus.Failure(
                DiskErrorKind.IoError,
                $"Resolved path escaped target folder for '{Redact.Path(fileName)}'."));
        }

        try
        {
            Directory.CreateDirectory(fullFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PartFileOpenFailure(DiskStatus.FromException(ex, fullFolder));
        }

        var partExistsBeforeOpen = File.Exists(partPath);
        if (resume && !partExistsBeforeOpen)
        {
            return new PartFileOpenFailure(DiskStatus.Failure(
                DiskErrorKind.PartFileMismatch,
                $"Part file '{Redact.Path(partPath)}' does not exist for resume."));
        }

        if (totalBytes is > 0)
        {
            var requiredBytes = resume && partExistsBeforeOpen
                ? Math.Max(0L, totalBytes.Value - alreadyWrittenBytes)
                : totalBytes.Value;

            if (requiredBytes > 0)
            {
                var provider = spaceProvider ?? WindowsDiskSpaceProvider.Instance;
                if (!provider.TryGetAvailableFreeBytes(fullFolder, out var availableBytes))
                {
                    return new PartFileOpenFailure(DiskStatus.Failure(
                        DiskErrorKind.IoError,
                        $"Could not query free space for '{Redact.Path(fullFolder)}'."));
                }

                if (availableBytes < requiredBytes)
                {
                    return new PartFileOpenFailure(DiskStatus.Failure(
                        DiskErrorKind.NotEnoughSpace,
                        $"Available free space ({availableBytes} B) is less than required ({requiredBytes} B) for '{Redact.Path(partPath)}'.",
                        Kernel32.ErrorDiskFull));
                }
            }
        }

        SafeFileHandle? handle = null;
        try
        {
            if (resume)
            {
                handle = File.OpenHandle(
                    partPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Read,
                    FileOptions.Asynchronous);

                var currentLength = RandomAccess.GetLength(handle);
                if (totalBytes is { } expectedLength && currentLength != expectedLength)
                {
                    handle.Dispose();
                    return new PartFileOpenFailure(DiskStatus.Failure(
                        DiskErrorKind.PartFileMismatch,
                        $"Part file '{Redact.Path(partPath)}' length ({currentLength} B) does not match expected ({expectedLength} B)."));
                }

                var existingSparse = (File.GetAttributes(partPath) & FileAttributes.SparseFile) != 0;
                return new PartFileOpened(new PartFileWriter(handle, partPath, finalPath, totalBytes, existingSparse));
            }

            handle = File.OpenHandle(
                partPath,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                FileOptions.Asynchronous);

            var isSparse = false;
            if (totalBytes is > 0)
            {
                if (OperatingSystem.IsWindows())
                {
                    isSparse = TrySetSparse(handle, setSparse: true, out _);
                }

                try
                {
                    if (setLengthFaultInjector?.Invoke() is { } injected)
                    {
                        throw injected;
                    }

                    RandomAccess.SetLength(handle, totalBytes.Value);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    handle.Dispose();
                    handle = null;
                    TryDeleteQuietly(partPath);
                    return new PartFileOpenFailure(DiskStatus.FromException(ex, partPath));
                }
            }

            return new PartFileOpened(new PartFileWriter(handle, partPath, finalPath, totalBytes, isSparse));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            handle?.Dispose();
            return new PartFileOpenFailure(DiskStatus.FromException(ex, partPath));
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<DiskStatus> WriteAsync(
        ReadOnlyMemory<byte> buffer,
        long offset,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsSealed)
        {
            throw new InvalidOperationException("Cannot write to a sealed PartFileWriter.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (TotalBytes is { } total && offset + buffer.Length > total)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                $"Write range [{offset}, {offset + buffer.Length}) exceeds TotalBytes ({total}).");
        }

        if (buffer.IsEmpty)
        {
            return DiskStatus.Success;
        }

        try
        {
            if (WriteFaultInjector?.Invoke(offset, buffer.Length) is { } injected)
            {
                throw injected;
            }

            await RandomAccess.WriteAsync(_handle, buffer, offset, cancellationToken).ConfigureAwait(false);
            return DiskStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DiskStatus.FromException(ex, PartPath);
        }
    }

    public async ValueTask<DiskStatus> FlushAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            await Task.Run(
                () =>
                {
                    if (FlushFaultInjector?.Invoke() is { } injected)
                    {
                        throw injected;
                    }

                    RandomAccess.FlushToDisk(_handle);
                },
                cancellationToken).ConfigureAwait(false);

            return DiskStatus.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DiskStatus.FromException(ex, PartPath);
        }
    }

    /// <summary>
    /// Phase 1 of finalisation: flushes buffered OS writes, verifies the file length and completeness, and clears the sparse
    /// attribute while keeping the handle open for verification or Mark-of-the-Web (TRD 6.4, ADR 0005).
    /// </summary>
    public async ValueTask<DiskStatus> SealAsync(
        long? expectedLength = null,
        long? bytesWritten = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var flushStatus = await FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!flushStatus.IsSuccess)
        {
            return flushStatus;
        }

        long actualLength;
        try
        {
            actualLength = RandomAccess.GetLength(_handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DiskStatus.FromException(ex, PartPath);
        }

        var targetLength = expectedLength ?? TotalBytes;
        if (targetLength is { } expected && actualLength != expected)
        {
            return DiskStatus.Failure(
                DiskErrorKind.LengthMismatch,
                $"Part file '{Redact.Path(PartPath)}' length ({actualLength} B) differs from expected ({expected} B).");
        }

        if (bytesWritten is { } written && written != actualLength)
        {
            return DiskStatus.Failure(
                DiskErrorKind.IncompleteData,
                $"Part file '{Redact.Path(PartPath)}' has {written} B recorded written out of {actualLength} B.");
        }

        if (IsSparse && actualLength > 0 && OperatingSystem.IsWindows())
        {
            if (TryCheckFullyAllocated(_handle, actualLength, out var fullyAllocated) && !fullyAllocated)
            {
                return DiskStatus.Failure(
                    DiskErrorKind.IncompleteData,
                    $"Part file '{Redact.Path(PartPath)}' still contains unallocated sparse holes.");
            }
        }

        if (IsSparse && OperatingSystem.IsWindows())
        {
            if (TrySetSparse(_handle, setSparse: false, out _))
            {
                IsSparse = false;
            }
        }

        IsSealed = true;
        return DiskStatus.Success;
    }

    /// <summary>
    /// Phase 2 of finalisation: closes the <c>.kasipart</c> handle and renames the file to <see cref="FinalPath"/> (TRD 6.4, ADR 0005).
    /// </summary>
    public ValueTask<DiskStatus> CommitAsync(
        bool overwriteDestination = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSealed)
        {
            return ValueTask.FromResult(DiskStatus.Failure(
                DiskErrorKind.IncompleteData,
                $"Part file '{Redact.Path(PartPath)}' must be sealed before commit."));
        }

        if (!overwriteDestination && File.Exists(FinalPath))
        {
            return ValueTask.FromResult(DiskStatus.Failure(
                DiskErrorKind.TargetExists,
                $"Destination '{Redact.Path(FinalPath)}' already exists."));
        }

        try
        {
            _handle.Dispose();
            File.Move(PartPath, FinalPath, overwrite: overwriteDestination);
            _committed = true;
            _disposed = true;
            return ValueTask.FromResult(DiskStatus.Success);
        }
        catch (IOException ex) when (!overwriteDestination && File.Exists(FinalPath))
        {
            return ValueTask.FromResult(DiskStatus.Failure(
                DiskErrorKind.TargetExists,
                $"Destination '{Redact.Path(FinalPath)}' already exists.",
                Kernel32.TryGetWin32ErrorCode(ex.HResult)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult(DiskStatus.FromException(ex, FinalPath));
        }
    }

    /// <summary>
    /// Closes the handle and deletes the <c>.kasipart</c> file when a job is cancelled or removed.
    /// </summary>
    public void Abandon()
    {
        if (!_disposed)
        {
            _handle.Dispose();
            _disposed = true;
        }

        if (!_committed)
        {
            TryDeleteQuietly(PartPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _handle.Dispose();
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private static bool IsValidBareFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        if (fileName is "." or ".."
            || Path.IsPathRooted(fileName)
            || fileName.AsSpan().IndexOfAny('/', '\\', ':') >= 0)
        {
            return false;
        }

        return string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal);
    }

    private static bool IsDirectChildOf(string childFullPath, string folderFullPath)
    {
        var parent = Path.GetDirectoryName(childFullPath);
        if (string.IsNullOrEmpty(parent))
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(parent),
            folderFullPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool TrySetSparse(SafeFileHandle handle, bool setSparse, out int nativeError)
    {
        var input = new Kernel32.FileSetSparseBuffer { SetSparse = setSparse ? (byte)1 : (byte)0 };
        var ok = Kernel32.DeviceIoControl(
            handle,
            Kernel32.FsctlSetSparse,
            &input,
            (uint)sizeof(Kernel32.FileSetSparseBuffer),
             null,
            0,
            out _,
            0);

        nativeError = ok ? 0 : Marshal.GetLastPInvokeError();
        return ok;
    }

    [SupportedOSPlatform("windows")]
    private static unsafe bool TryCheckFullyAllocated(SafeFileHandle handle, long fileLength, out bool fullyAllocated)
    {
        const int bufferCapacity = 64;
        Span<Kernel32.FileAllocatedRangeBuffer> ranges = stackalloc Kernel32.FileAllocatedRangeBuffer[bufferCapacity];

        long expectedNextOffset = 0;
        while (expectedNextOffset < fileLength)
        {
            var query = new Kernel32.FileAllocatedRangeBuffer
            {
                FileOffset = expectedNextOffset,
                Length = fileLength - expectedNextOffset,
            };

            bool ok;
            uint bytesReturned;
            fixed (Kernel32.FileAllocatedRangeBuffer* pRanges = ranges)
            {
                ok = Kernel32.DeviceIoControl(
                    handle,
                    Kernel32.FsctlQueryAllocatedRanges,
                    &query,
                    (uint)sizeof(Kernel32.FileAllocatedRangeBuffer),
                    pRanges,
                    (uint)(bufferCapacity * sizeof(Kernel32.FileAllocatedRangeBuffer)),
                    out bytesReturned,
                    0);
            }

            var error = ok ? 0 : Marshal.GetLastPInvokeError();
            if (!ok && error != Kernel32.ErrorMoreData)
            {
                fullyAllocated = false;
                return false;
            }

            var count = (int)(bytesReturned / (uint)sizeof(Kernel32.FileAllocatedRangeBuffer));
            if (count == 0)
            {
                fullyAllocated = false;
                return true;
            }

            for (var i = 0; i < count; i++)
            {
                var range = ranges[i];
                if (range.FileOffset > expectedNextOffset)
                {
                    fullyAllocated = false;
                    return true;
                }

                var rangeEnd = range.FileOffset + range.Length;
                if (rangeEnd > expectedNextOffset)
                {
                    expectedNextOffset = rangeEnd;
                }
            }

            if (ok)
            {
                break;
            }
        }

        fullyAllocated = expectedNextOffset >= fileLength;
        return true;
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
