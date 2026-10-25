using System.Runtime.InteropServices;
using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// Reads the active power plan's RTC-wake setting through <c>PowerReadACValueIndex</c>/<c>PowerReadDCValueIndex</c> so the Scheduler
/// can warn when wake timers are off (KASI-QUE-005, TRD 10.2).
/// </summary>
public sealed partial class WindowsWakeTimerPolicy : IWakeTimerPolicy
{
    private static readonly Guid SleepSubgroup = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    private static readonly Guid AllowRtcWake = new("BD3B718A-0680-4D9D-8AB2-E1D2B4AC806D");

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadACValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint valueIndex);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerReadDCValueIndex(nint rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint valueIndex);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    public WakeTimerPolicy Check()
    {
        if (!OperatingSystem.IsWindows())
        {
            return WakeTimerPolicy.Unknown;
        }

        if (PowerGetActiveScheme(0, out var scheme) != 0)
        {
            return WakeTimerPolicy.Unknown;
        }

        try
        {
            var schemeGuid = Marshal.PtrToStructure<Guid>(scheme);
            var subgroup = SleepSubgroup;
            var setting = AllowRtcWake;
            var ac = PowerReadACValueIndex(0, ref schemeGuid, ref subgroup, ref setting, out var acValue);
            subgroup = SleepSubgroup;
            setting = AllowRtcWake;
            var dc = PowerReadDCValueIndex(0, ref schemeGuid, ref subgroup, ref setting, out var dcValue);
            if (ac != 0 && dc != 0)
            {
                return WakeTimerPolicy.Unknown;
            }

            var allowed = (ac == 0 && acValue == 1) || (dc == 0 && dcValue == 1);
            if (allowed)
            {
                return WakeTimerPolicy.Allowed;
            }

            return (ac == 0 && acValue == 0) || (dc == 0 && dcValue == 0)
                ? WakeTimerPolicy.Disabled
                : WakeTimerPolicy.Unknown;
        }
        finally
        {
            _ = LocalFree(scheme);
        }
    }
}
