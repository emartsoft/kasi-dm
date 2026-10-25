using System.ComponentModel;
using System.Runtime.InteropServices;
using Kasi.Core;

namespace Kasi.Scheduling;

/// <summary>
/// The Win32 waitable timer behind <see cref="IWakeTimer"/>: <c>SetWaitableTimer</c> with the resume flag so the timer can wake the PC
/// from sleep (KASI-QUE-005, TRD 10.2). A worker waits on the handle and raises <see cref="Fired"/>.
/// </summary>
public sealed partial class WindowsWakeTimer : IWakeTimer
{
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWaitableTimerExW(nint attributes, string? name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(nint timer, ref long dueTime, int period, nint completion, nint argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelWaitableTimer(nint timer);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private readonly Lock _lock = new();
    private nint _handle;
    private Task? _waiter;
    private bool _disposed;
    private DateTimeOffset? _armedFor;

    public event EventHandler? Fired;

    public DateTimeOffset? ArmedFor
    {
        get
        {
            lock (_lock)
            {
                return _armedFor;
            }
        }
    }

    public void Arm(DateTimeOffset wakeAtUtc)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureHandle();
            var due = wakeAtUtc.UtcDateTime.ToFileTimeUtc();
            if (!SetWaitableTimer(_handle, ref due, 0, 0, 0, true))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            _armedFor = wakeAtUtc.ToUniversalTime();
        }
    }

    public void Disarm()
    {
        lock (_lock)
        {
            if (_handle != 0)
            {
                _ = CancelWaitableTimer(_handle);
            }

            _armedFor = null;
        }
    }

    private void EnsureHandle()
    {
        if (_handle != 0)
        {
            return;
        }

        _handle = CreateWaitableTimerExW(0, null, 0, TimerAllAccess);
        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _waiter = Task.Run(() =>
        {
            while (true)
            {
                var result = WaitForSingleObject(_handle, Infinite);
                if (_disposed || result != 0)
                {
                    return;
                }

                lock (_lock)
                {
                    _armedFor = null;
                }

                Fired?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void Dispose()
    {
        nint handle;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _armedFor = null;
            handle = _handle;
            _handle = 0;
        }

        if (handle != 0)
        {
            _ = CancelWaitableTimer(handle);
            _ = CloseHandle(handle);
        }

        _ = _waiter;
    }
}
