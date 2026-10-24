namespace Kasi.Core;

/// <summary>Abstracts system power actions to allow safe testing without triggering real OS shutdowns (TRD 10.3, KASI-QUE-004).</summary>
public interface IPowerController
{
    Task SleepAsync(CancellationToken cancellationToken = default);

    Task HibernateAsync(CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
