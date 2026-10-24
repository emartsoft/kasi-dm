namespace Kasi.Media;

/// <summary>
/// A parsed HLS/DASH key signal. <see cref="Method"/> is the HLS method (NONE, AES-128, SAMPLE-AES, …) or the DASH
/// <c>ContentProtection</c> scheme id; the protection check in M3.2 decides whether the presentation is playable.
/// </summary>
public sealed record MediaKey(
    string Method,
    Uri? Uri = null,
    string? KeyFormat = null,
    string? KeyFormatVersions = null,
    string? Iv = null);
