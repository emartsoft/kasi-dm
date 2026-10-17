namespace Kasi.Core;

/// <summary>Read access to queues. Implemented in <c>Kasi.Data</c>; defined here so the engine never references the data layer.</summary>
public interface IQueueRepository
{
    Task<Queue?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>All queues in display order (<see cref="Queue.SortOrder"/>, then id).</summary>
    Task<IReadOnlyList<Queue>> ListAsync(CancellationToken cancellationToken = default);
}
