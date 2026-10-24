namespace Kasi.Core;

/// <summary>Read access to categories. Implemented in <c>Kasi.Data</c>.</summary>
public interface ICategoryRepository
{
    Task<Category?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>All categories ordered by id, so the built-in ones keep their documented order.</summary>
    Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a user category and returns its id. The category's own <see cref="Category.Id"/> and <see cref="Category.IsBuiltin"/> are ignored.</summary>
    Task<int> AddAsync(Category category, CancellationToken cancellationToken = default);

    /// <summary>Changes the name and the folder of a category; false if it does not exist. A built-in category keeps its name, only its folder changes.</summary>
    Task<bool> UpdateAsync(Category category, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a user category with its rules; the jobs that were in it keep their folder and fall back to no category. False if it does not
    /// exist or ships with Kasi.
    /// </summary>
    Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
