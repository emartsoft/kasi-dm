using Kasi.Engine.Interop;

namespace Kasi.Engine;

/// <summary>
/// Default <see cref="IDiskSpaceProvider"/> backed by Win32 <c>GetDiskFreeSpaceExW</c> on the target directory path,
/// so folder-mounted VHDs, junctions, UNC paths and per-user quotas are handled accurately.
/// </summary>
public sealed class WindowsDiskSpaceProvider : IDiskSpaceProvider
{
    public static WindowsDiskSpaceProvider Instance { get; } = new();

    public bool TryGetAvailableFreeBytes(string directoryPath, out long availableBytes)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            availableBytes = 0;
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(directoryPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            availableBytes = 0;
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            if (Kernel32.GetDiskFreeSpaceEx(fullPath, out var freeToCaller, out _, out _))
            {
                availableBytes = freeToCaller > long.MaxValue ? long.MaxValue : (long)freeToCaller;
                return true;
            }

            availableBytes = 0;
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root))
            {
                availableBytes = 0;
                return false;
            }

            var drive = new DriveInfo(root);
            availableBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            availableBytes = 0;
            return false;
        }
    }
}
