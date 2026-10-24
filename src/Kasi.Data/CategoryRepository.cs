using Dapper;
using Kasi.Core;

namespace Kasi.Data;

public sealed class CategoryRepository(SqliteConnectionFactory factory) : ICategoryRepository
{
    private const string Select = "SELECT id AS Id, name AS Name, folder AS Folder, is_builtin AS IsBuiltin FROM category";

    public async Task<Category?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<Row>(
            new CommandDefinition($"{Select} WHERE id = @id;", new { id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row?.ToCategory();
    }

    public async Task<IReadOnlyList<Category>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(
            new CommandDefinition($"{Select} ORDER BY id;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToCategory())];
    }

    public async Task<int> AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO category (name, folder, is_builtin) VALUES (@Name, @Folder, 0); SELECT last_insert_rowid();",
            new { category.Name, category.Folder },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return checked((int)id);
    }

    public async Task<bool> UpdateAsync(Category category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE category SET name = CASE WHEN is_builtin = 1 THEN name ELSE @Name END, folder = @Folder WHERE id = @Id;",
            new { category.Id, category.Name, category.Folder },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE job SET category_id = NULL WHERE category_id = @id AND EXISTS (SELECT 1 FROM category WHERE id = @id AND is_builtin = 0);",
            new { id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM category WHERE id = @id AND is_builtin = 0;", new { id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed > 0;
    }

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
#pragma warning restore CA1812
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string Folder { get; set; } = string.Empty;

        public long IsBuiltin { get; set; }

        public Category ToCategory() => new() { Id = checked((int)Id), Name = Name, Folder = Folder, IsBuiltin = IsBuiltin != 0 };
    }
}
