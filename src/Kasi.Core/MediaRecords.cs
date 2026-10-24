namespace Kasi.Core;

/// <summary>One chosen stream of a media job (TRD 7.4, 11.2): the video, audio or subtitle rendition being captured.</summary>
public sealed record MediaStreamRecord(
    int Index,
    string Role,
    string? Language = null,
    string? Codecs = null,
    string? Container = null,
    string? VariantId = null,
    long? EstimatedBytes = null,
    string? InitUrl = null,
    long? InitSize = null);

/// <summary>One segment row; <see cref="Completed"/> is set only after the segment file is flushed and renamed.</summary>
public sealed record MediaSegmentRecord(
    int StreamIndex,
    int Index,
    string Url,
    long? Size = null,
    bool Completed = false,
    string? Iv = null,
    bool Discontinuity = false,
    long? RangeStart = null,
    long? RangeEnd = null);

/// <summary>A batch item handed to <see cref="IMediaRepository.MarkCompletedAsync"/>.</summary>
public sealed record MediaSegmentCompletion(int StreamIndex, int Index, long Size);
