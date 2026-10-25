namespace Kasi.Core;

/// <summary>Arms a Windows waitable timer that can wake the PC for the next scheduled queue start (KASI-QUE-005, TRD 10.2).</summary>
public interface IWakeTimer : IDisposable
{
    /// <summary>Arms the timer for the next start; a later call replaces the previous one.</summary>
    void Arm(DateTimeOffset wakeAtUtc);

    void Disarm();

    /// <summary>The armed instant, or null when disarmed.</summary>
    DateTimeOffset? ArmedFor { get; }

    /// <summary>Raised on a worker thread when the hardware timer fires.</summary>
    event EventHandler? Fired;
}

/// <summary>Whether the active Windows power plan allows wake timers (the Scheduler window's warning, KASI-QUE-005).</summary>
public enum WakeTimerPolicy
{
    Allowed,
    Disabled,
    Unknown,
}

public interface IWakeTimerPolicy
{
    WakeTimerPolicy Check();
}
