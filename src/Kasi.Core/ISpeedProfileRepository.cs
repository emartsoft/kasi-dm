namespace Kasi.Core;

/// <summary>Persists named speed profiles and the active scheduled profile (KASI-BWL-003).</summary>
public interface ISpeedProfileRepository
{
    Task<IReadOnlyList<SpeedProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task<SpeedProfile?> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<int> AddAsync(SpeedProfile profile, CancellationToken cancellationToken = default);

    Task<bool> UpdateAsync(SpeedProfile profile, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The profile currently applied by the schedule, or null for a manual/unlimited limit.</summary>
    Task<int?> GetActiveProfileIdAsync(CancellationToken cancellationToken = default);

    Task SetActiveProfileIdAsync(int? profileId, CancellationToken cancellationToken = default);
}
