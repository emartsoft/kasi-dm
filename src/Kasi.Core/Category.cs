namespace Kasi.Core;

/// <summary>Where files of a kind are saved: the <c>category</c> table (TRD 10.4, 11.2). Matching rules arrive with KASI-CAT-002.</summary>
public sealed record Category
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public required string Folder { get; init; }

    public bool IsBuiltin { get; init; }
}
