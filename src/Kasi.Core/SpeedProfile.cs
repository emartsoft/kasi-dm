namespace Kasi.Core;

/// <summary>A named bandwidth cap (KASI-BWL-001/003). A null <see cref="LimitBps"/> means unlimited.</summary>
public sealed record SpeedProfile
{
    public required int Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Bytes per second; null means unlimited.</summary>
    public long? LimitBps { get; init; }

    public required int SortOrder { get; init; }
}

/// <summary>The one global speed limit the scheduler and the tray adjust (KASI-BWL-001/003).</summary>
public interface ISpeedLimitTarget
{
    /// <summary>Bytes per second; null means unlimited.</summary>
    long? SpeedLimit { get; set; }
}
