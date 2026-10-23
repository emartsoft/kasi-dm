namespace Kasi.Core;

/// <summary>Storage for queue operating schedules (<c>queue_schedule</c> table).</summary>
public interface IQueueScheduleRepository
{
    Task<QueueSchedule?> GetAsync(int queueId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<QueueSchedule>> ListAsync(CancellationToken cancellationToken = default);

    Task UpsertAsync(QueueSchedule schedule, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int queueId, CancellationToken cancellationToken = default);
}
