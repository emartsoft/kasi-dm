using Dapper;
using Kasi.Core;

namespace Kasi.Data;

public sealed class CategoryRuleRepository(SqliteConnectionFactory factory) : ICategoryRuleRepository
{
    private const string Select =
        "SELECT id AS Id, category_id AS CategoryId, kind AS Kind, pattern AS Pattern, position AS Position, is_builtin AS IsBuiltin, is_enabled AS IsEnabled FROM category_rule";

    public async Task<IReadOnlyList<CategoryRule>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<Row>(
            new CommandDefinition($"{Select} ORDER BY position, id;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToRule())];
    }

    public async Task<int> AddAsync(CategoryRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO category_rule (category_id, kind, pattern, position, is_builtin, is_enabled) " +
            "VALUES (@CategoryId, @Kind, @Pattern, @Position, 0, @IsEnabled); SELECT last_insert_rowid();",
            new { rule.CategoryId, Kind = KindText(rule.Kind), rule.Pattern, rule.Position, IsEnabled = rule.IsEnabled ? 1 : 0 },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return checked((int)id);
    }

    public async Task<bool> UpdateAsync(CategoryRule rule, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE category_rule SET category_id = @CategoryId, pattern = @Pattern, position = @Position, is_enabled = @IsEnabled WHERE id = @Id;",
            new { rule.Id, rule.CategoryId, rule.Pattern, rule.Position, IsEnabled = rule.IsEnabled ? 1 : 0 },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var changed = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM category_rule WHERE id = @id AND is_builtin = 0;", new { id }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return changed > 0;
    }

    private static string KindText(CategoryRuleKind kind) => kind switch
    {
        CategoryRuleKind.Extension => "extension",
        CategoryRuleKind.Domain => "domain",
        CategoryRuleKind.Url => "url",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

#pragma warning disable CA1812 // created by Dapper
    private sealed class Row
#pragma warning restore CA1812
    {
        public long Id { get; set; }

        public long CategoryId { get; set; }

        public string Kind { get; set; } = string.Empty;

        public string Pattern { get; set; } = string.Empty;

        public long Position { get; set; }

        public long IsBuiltin { get; set; }

        public long IsEnabled { get; set; }

        public CategoryRule ToRule() => new()
        {
            Id = checked((int)Id),
            CategoryId = checked((int)CategoryId),
            Kind = Kind switch
            {
                "extension" => CategoryRuleKind.Extension,
                "domain" => CategoryRuleKind.Domain,
                "url" => CategoryRuleKind.Url,
                _ => throw new InvalidDataException("Unknown category rule kind."),
            },
            Pattern = Pattern,
            Position = checked((int)Position),
            IsBuiltin = IsBuiltin != 0,
            IsEnabled = IsEnabled != 0,
        };
    }
}
