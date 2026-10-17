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
