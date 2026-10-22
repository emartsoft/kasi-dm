namespace Kasi.App.Startup;

/// <summary>The per-user instance lock (KASI-SYS-005): a mutex in the session namespace, held by the first instance for its lifetime.</summary>
internal sealed class InstanceLock : IDisposable
{
    private readonly Mutex _mutex;

    private InstanceLock(Mutex mutex) => _mutex = mutex;

    /// <summary>The lock, or null when another instance of this user holds it. Release it on the thread that took it (it is released on dispose).</summary>
    public static InstanceLock? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var created);
        if (created)
        {
            return new InstanceLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
