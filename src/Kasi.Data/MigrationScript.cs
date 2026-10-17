namespace Kasi.Data;

internal sealed record MigrationScript(int Version, string Name, string Sql);
