namespace Kasi.Core;

#pragma warning disable CA1711 // "Queue" is the domain name from TRD 5.2 and the table name, not a collection type
/// <summary>A queue of jobs and the limits it applies: the <c>queue</c> table (TRD 10.1, 11.2). Defaults match the column defaults.</summary>
public sealed record Queue
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    public int MaxParallel { get; init; } = 4;

    /// <summary>Null means the global default.</summary>
    public int? Connections { get; init; }

    /// <summary>Null means unlimited.</summary>
    public long? SpeedLimitBps { get; init; }

    public long? QuotaBytes { get; init; }

    public QuotaReset? QuotaReset { get; init; }

    // ponytail: plain text until M2 gives post-completion actions a type (KASI-QUE-004); the column default is 'none'.
    public string PostAction { get; init; } = "none";

    public required int SortOrder { get; init; }
}
#pragma warning restore CA1711
