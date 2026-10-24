namespace Kasi.Media;

/// <summary>Hard bounds applied before any manifest can allocate unbounded memory (TPL 7.2).</summary>
public static class MediaLimits
{
    /// <summary>The plan's manifest cap, measured in characters and in UTF-8 bytes of the manifest text.</summary>
    public const int MaxManifestBytes = 10 * 1024 * 1024;

    public const int MaxVariants = 1_000;

    public const int MaxRenditions = 1_000;

    public const int MaxSegmentsPerStream = 100_000;

    public const int MaxSegmentsTotal = 250_000;

    public const int MaxTimelineEntries = 100_000;

    public const int MaxBaseUrlDepth = 8;

    public const int MaxPeriods = 32;

    public const int MaxAdaptationSets = 256;
}

/// <summary>The machine-readable reason a manifest was rejected.</summary>
public enum MediaParseError
{
    Empty,
    MissingHeader,
    InvalidTag,
    InvalidAttribute,
    InvalidUri,
    InvalidNumber,
    InvalidXml,
    TooLarge,
    TooManyItems,
    Unsupported,
    InvalidPlaylist,
}

/// <summary>The only exception the public parsers throw for malformed or hostile input.</summary>
public sealed class MediaParseException : Exception
{
    public MediaParseException(MediaParseError error, string message)
        : base(message)
    {
        Error = error;
    }

    public MediaParseException(MediaParseError error, string message, Exception inner)
        : base(message, inner)
    {
        Error = error;
    }

    public MediaParseError Error { get; }
}
