namespace Kasi.Core;

/// <summary>Storage for queues. Implemented in <c>Kasi.Data</c>; defined here so the engine and scheduler never reference the data layer.</summary>
public interface IQueueRepository
{
    Task<Queue?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>All queues in display order (<see cref="Queue.SortOrder"/>, then id).</summary>
    Task<IReadOnlyList<Queue>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a new queue and returns its assigned id.</summary>
    Task<int> AddAsync(Queue queue, CancellationToken cancellationToken = default);

    /// <summary>Updates queue properties. Returns false if the queue does not exist.</summary>
    Task<bool> UpdateAsync(Queue queue, CancellationToken cancellationToken = default);

    /// <summary>Deletes a user-created queue. The main queue (id = 1) cannot be deleted.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
