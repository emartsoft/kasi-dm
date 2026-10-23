using System.ComponentModel;
using System.Runtime.InteropServices;
using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>Invokes Windows power management APIs (<c>SetSuspendState</c>, <c>InitiateShutdown</c>) (TRD 10.3).</summary>
public sealed partial class WindowsPowerController : IPowerController
{
    private const uint ShutdownPoweroff = 0x00000008;
    private const uint ShutdownForceOthers = 0x00000001;

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState(
        [MarshalAs(UnmanagedType.Bool)] bool hibernate,
        [MarshalAs(UnmanagedType.Bool)] bool forceCritical,
        [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    [LibraryImport("advapi32.dll", EntryPoint = "InitiateShutdownW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint InitiateShutdown(
        string? machineName,
        string? message,
        uint gracePeriod,
        uint shutdownFlags,
        uint reason);

    public Task SleepAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        return Task.CompletedTask;
    }

    public Task HibernateAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!SetSuspendState(hibernate: true, forceCritical: false, disableWakeEvent: false))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            uint result = InitiateShutdown(machineName: null, message: null, gracePeriod: 0, shutdownFlags: ShutdownPoweroff | ShutdownForceOthers, reason: 0);
            if (result != 0)
            {
                throw new Win32Exception(checked((int)result));
            }
        }

        return Task.CompletedTask;
    }
}
