namespace Kasi.Core;

/// <summary>
/// What the person chose in the media picker, kept stable across manifest reloads: signed or rotating playlist URLs change
/// between runs, so a quality is identified by its height and bitrate, never by its URL.
/// </summary>
/// <param name="Format">Mp4, Matroska or AudioOnly.</param>
public sealed record MediaSelection(string Format, int? Height = null, int? Bandwidth = null, string? Codecs = null, string? Language = null);
