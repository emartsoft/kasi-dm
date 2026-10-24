namespace Kasi.Media;

/// <summary>An initialization segment (HLS <c>EXT-X-MAP</c>, DASH <c>Initialization</c>).</summary>
public sealed record MediaInitSegment(Uri Uri, MediaByteRange? Range = null);
