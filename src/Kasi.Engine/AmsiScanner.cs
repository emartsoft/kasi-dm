using System.Buffers;
using System.Runtime.InteropServices;
using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// Scans through the Windows Antimalware Scan Interface, so whatever antivirus is installed (Defender or another) answers
/// (KASI-CAT-004). The file is read in windows of 4 MiB that overlap by 64 KiB, so a signature that falls on a window's edge is still seen;
/// files above 512 MiB are left to Windows' own on-access scanning and the log says so. Anything that stops the scan from running
/// (no amsi.dll, no provider, an error code) is <see cref="ScanVerdict.NotScanned"/>.
/// </summary>
public sealed partial class AmsiScanner : IFileScanner
{
    public const int WindowBytes = 4 * 1024 * 1024;
    public const int OverlapBytes = 64 * 1024;
    public const long MaxFileBytes = 512L * 1024 * 1024;

    private const int DetectedAtLeast = 32768; // AMSI_RESULT_DETECTED

    public bool IsEnabled => true;

    public Task<ScanResult> ScanAsync(string path, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(path, cancellationToken), cancellationToken);

    /// <summary>Scans one buffer, as one piece of content. Used by the file scan and by the test that feeds it the EICAR string without a file.</summary>
    public static ScanResult ScanBuffer(ReadOnlySpan<byte> data, string contentName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ScanResult.NotScanned("AMSI is not available on this system");
        }

        try
        {
            if (AmsiInitialize("Kasi", out var context) < 0)
            {
                return ScanResult.NotScanned("AMSI could not start");
            }

            try
            {
                if (AmsiOpenSession(context, out var session) < 0)
                {
                    return ScanResult.NotScanned("AMSI could not start a scan");
                }

                try
                {
                    return ScanWindow(context, session, data, contentName);
                }
                finally
                {
                    AmsiCloseSession(context, session);
                }
            }
            finally
            {
                AmsiUninitialize(context);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ScanResult.NotScanned("AMSI is not available on this system");
        }
    }

    private static ScanResult Scan(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ScanResult.NotScanned("AMSI is not available on this system");
        }

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = RandomAccess.GetLength(handle);
            if (length > MaxFileBytes)
            {
                return ScanResult.NotScanned("file too large to scan here");
            }

            if (length == 0)
            {
                return ScanResult.NotScanned("nothing to scan");
            }

            var name = Path.GetFileName(path);
            if (AmsiInitialize("Kasi", out var context) < 0)
            {
                return ScanResult.NotScanned("AMSI could not start");
            }

            try
            {
                if (AmsiOpenSession(context, out var session) < 0)
                {
                    return ScanResult.NotScanned("AMSI could not start a scan");
                }

                try
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(WindowBytes);
                    try
                    {
                        var scanned = false;
                        for (long offset = 0; offset < length; offset += WindowBytes - OverlapBytes)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(WindowBytes, length - offset)), offset);
                            if (read == 0)
                            {
                                break;
                            }

                            var result = ScanWindow(context, session, buffer.AsSpan(0, read), name);
                            if (result.Verdict != ScanVerdict.Clean)
                            {
                                return result;
                            }

                            scanned = true;
                            if (offset + read >= length)
                            {
                                break;
                            }
                        }

                        return scanned ? ScanResult.Clean : ScanResult.NotScanned("nothing to scan");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }
                finally
                {
                    AmsiCloseSession(context, session);
                }
            }
            finally
            {
                AmsiUninitialize(context);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return ScanResult.NotScanned("AMSI is not available on this system");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ScanResult.NotScanned("the file could not be read for the scan");
        }
    }

    private static unsafe ScanResult ScanWindow(nint context, nint session, ReadOnlySpan<byte> data, string contentName)
    {
        fixed (byte* pointer = data)
        {
            var hr = AmsiScanBuffer(context, (nint)pointer, (uint)data.Length, contentName, session, out var result);
            if (hr < 0)
            {
                return ScanResult.NotScanned("the antivirus could not scan");
            }

            return result >= DetectedAtLeast ? ScanResult.Detected : ScanResult.Clean;
        }
    }

    [LibraryImport("amsi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AmsiInitialize(string appName, out nint amsiContext);

    [LibraryImport("amsi.dll")]
    private static partial void AmsiUninitialize(nint amsiContext);

    [LibraryImport("amsi.dll")]
    private static partial int AmsiOpenSession(nint amsiContext, out nint amsiSession);

    [LibraryImport("amsi.dll")]
    private static partial void AmsiCloseSession(nint amsiContext, nint amsiSession);

    [LibraryImport("amsi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AmsiScanBuffer(nint amsiContext, nint buffer, uint length, string contentName, nint amsiSession, out int result);
}
