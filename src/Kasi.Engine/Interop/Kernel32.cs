using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Kasi.Engine.Interop;

internal static partial class Kernel32
{
    internal const uint FsctlSetSparse = 0x000900C4;
    internal const uint FsctlQueryAllocatedRanges = 0x000940CF;

    internal const int ErrorInvalidFunction = 1;
    internal const int ErrorHandleDiskFull = 39;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorDiskFull = 112;
    internal const int ErrorMoreData = 234;
    internal const int ErrorDiskQuotaExceeded = 1295;
    internal const int ErrorNotEnoughQuota = 1816;

    internal const int HResultHandleDiskFull = unchecked((int)0x80070027);
    internal const int HResultDiskFull = unchecked((int)0x80070070);
    internal const int HResultDiskQuotaExceeded = unchecked((int)0x8007050F);
    internal const int HResultNotEnoughQuota = unchecked((int)0x80070718);
    internal const int StatusQuotaExceeded = unchecked((int)0xC0000044);
    internal const int StatusDiskFull = unchecked((int)0xC000007F);

    internal static int? TryGetWin32ErrorCode(int hResult) =>
        (hResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            ? hResult & 0xFFFF
            : null;

    internal static bool IsOutOfSpaceHResult(int hResult)
    {
        if (hResult is HResultDiskFull or HResultHandleDiskFull or HResultDiskQuotaExceeded or HResultNotEnoughQuota or StatusDiskFull or StatusQuotaExceeded)
        {
            return true;
        }

        return TryGetWin32ErrorCode(hResult) is ErrorDiskFull or ErrorHandleDiskFull or ErrorDiskQuotaExceeded or ErrorNotEnoughQuota;
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        nint lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileSetSparseBuffer
    {
        public byte SetSparse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAllocatedRangeBuffer
    {
        public long FileOffset;
        public long Length;
    }
}
