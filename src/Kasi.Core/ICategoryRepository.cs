namespace Kasi.Core;

/// <summary>Read access to categories. Implemented in <c>Kasi.Data</c>.</summary>
public interface ICategoryRepository
{
    Task<Category?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>All categories ordered by id, so the built-in ones keep their documented order.</summary>
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default);
}
