namespace Kasi.Engine;

/// <summary>
/// Queries available free bytes for a target directory, respecting mount points, UNC shares and per-user disk quotas (TRD 6.4).
/// </summary>
public interface IDiskSpaceProvider
{
    bool TryGetAvailableFreeBytes(string directoryPath, out long availableBytes);
}
