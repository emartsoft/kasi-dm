namespace Kasi.App.ViewModels;

/// <summary>The last minute of the total download speed, one sample a second, oldest first (the sidebar's aggregate-speed chart).</summary>
internal sealed class SpeedHistory(int capacity = SpeedHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 60;

    private readonly Queue<double> _samples = new(capacity);

    public int Capacity { get; } = capacity;

    /// <summary>A copy of the samples, so a chart bound to it never sees the list change under it.</summary>
    public IReadOnlyList<double> Snapshot() => [.. _samples];

    public void Add(double bytesPerSecond)
    {
        if (_samples.Count >= Capacity)
        {
            _samples.Dequeue();
        }

        _samples.Enqueue(Math.Max(0, bytesPerSecond));
    }
}
