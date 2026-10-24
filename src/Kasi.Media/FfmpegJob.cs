using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Kasi.Media;

/// <summary>
/// A Win32 job object with <c>KILL_ON_JOB_CLOSE</c>. Closing the handle (including when the Kasi process exits) terminates
/// the FFmpeg child and its descendants, so a crash cannot leave an orphaned muxer (TRD 13.4).
/// </summary>
internal sealed class FfmpegJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    private IntPtr _handle;

    private FfmpegJob(IntPtr handle) => _handle = handle;

    public static FfmpegJob Assign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            throw new FfmpegException(FfmpegErrorKind.Unavailable, "Could not create the FFmpeg job object.", new Win32Exception());
        }

        var job = new FfmpegJob(handle);
        try
        {
            var limits = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = KillOnJobClose },
            };
            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, ExtendedLimitInformation, buffer, (uint)size))
                {
                    throw new Win32Exception();
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (!AssignProcessToJobObject(handle, process.Handle))
            {
                if (process.HasExited)
                {
                    return job; // the process finished before it could be assigned; there is nothing left to kill
                }

                throw new Win32Exception();
            }

            return job;
        }
        catch
        {
            job.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
