using System.Text.Json.Serialization;

namespace Kasi.Ipc.Messages;

/// <summary>
/// A protocol message (TRD 9.4). The wire shape is a flat JSON object with <c>v</c>, <c>id</c> and a <c>type</c> that picks the record.
/// URLs and cookie values are secrets: nothing here puts them in an exception message.
/// </summary>
[JsonPolymorphic(
    TypeDiscriminatorPropertyName = "type",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
    IgnoreUnrecognizedTypeDiscriminators = false)]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Rules), "rules")]
[JsonDerivedType(typeof(AddDownload), "addDownload")]
[JsonDerivedType(typeof(AddBatch), "addBatch")]
[JsonDerivedType(typeof(MediaDetected), "mediaDetected")]
[JsonDerivedType(typeof(CaptureMedia), "captureMedia")]
[JsonDerivedType(typeof(RefreshLink), "refreshLink")]
[JsonDerivedType(typeof(LinkRefreshed), "linkRefreshed")]
[JsonDerivedType(typeof(OpenSettings), "openSettings")]
[JsonDerivedType(typeof(Ack), "ack")]
[JsonDerivedType(typeof(IpcError), "error")]
public abstract record IpcMessage
{
    /// <summary>The protocol version; it must be on the wire and equal <see cref="IpcProtocol.Version"/>.</summary>
    [JsonRequired]
    public int V { get; init; } = IpcProtocol.Version;

    /// <summary>The correlation id; a reply carries the id of the message it answers.</summary>
    public required string Id { get; init; }
}

/// <summary>A cookie the browser holds for the download (TRD 9.4). The value is a secret.</summary>
public sealed record IpcCookie
{
    public required string Name { get; init; }

    public required string Value { get; init; }

    public required string Domain { get; init; }

    public required string Path { get; init; }

    public required bool Secure { get; init; }

    public required bool HttpOnly { get; init; }
}

public sealed record IpcLink
{
    public required string Url { get; init; }

    public string? Filename { get; init; }
}

/// <summary>Extension to Kasi: the version handshake; Kasi replies with <see cref="Rules"/>.</summary>
public sealed record Hello : IpcMessage
{
    public required BrowserClient Client { get; init; }

    public required string ExtensionVersion { get; init; }

    public required int ProtocolMin { get; init; }

    public required int ProtocolMax { get; init; }
}

/// <summary>Kasi to extension: interception rules, bypass key, enabled flag.</summary>
public sealed record Rules : IpcMessage
{
    public required bool Enabled { get; init; }

    public required IReadOnlyList<string> Extensions { get; init; }

    public required IReadOnlyList<string> MimeTypes { get; init; }

    public required IReadOnlyList<string> ExcludedSites { get; init; }

    public required BypassKey BypassKey { get; init; }

    public required int AckTimeoutMs { get; init; }

    /// <summary>Optional minimum size in bytes (TRD 9.2); send it only to an extension whose <c>hello</c> reports <see cref="IpcProtocol.MinSizeExtensionVersion"/> or later.</summary>
    public long? MinSizeBytes { get; init; }
}

/// <summary>Extension to Kasi: one intercepted or context-menu download.</summary>
public sealed record AddDownload : IpcMessage
{
    public required string Url { get; init; }

    public required string FinalUrl { get; init; }

    public string? Referrer { get; init; }

    public required string Filename { get; init; }

    public required string Mime { get; init; }

    public required string UserAgent { get; init; }

    public required IReadOnlyList<IpcCookie> Cookies { get; init; }
}

/// <summary>Extension to Kasi: the links chosen in the picker (KASI-BRW-005).</summary>
public sealed record AddBatch : IpcMessage
{
    public required string Referrer { get; init; }

    public required IReadOnlyList<IpcLink> Links { get; init; }

    public required IReadOnlyList<IpcCookie> Cookies { get; init; }

    public required string UserAgent { get; init; }
}

/// <summary>Extension to Kasi: a manifest or direct media file found in a tab.</summary>
public sealed record MediaDetected : IpcMessage
{
    public required int TabId { get; init; }

    public required string PageUrl { get; init; }

    public required MediaKind Kind { get; init; }

    public required string Url { get; init; }

    public string? Title { get; init; }
}

/// <summary>Extension to Kasi: the user chose a stream from the popup or overlay.</summary>
public sealed record CaptureMedia : IpcMessage
{
    public required int TabId { get; init; }

    public required string Url { get; init; }

    public required MediaKind Kind { get; init; }

    public required IReadOnlyList<IpcCookie> Cookies { get; init; }

    public required string Referrer { get; init; }

    public required string UserAgent { get; init; }
}

/// <summary>Kasi to extension: open the referrer page and watch for a matching download (TRD 6.6).</summary>
public sealed record RefreshLink : IpcMessage
{
    public required string JobId { get; init; }

    public required string ReferrerUrl { get; init; }

    public string? MatchHint { get; init; }
}

/// <summary>Extension to Kasi: the fresh URL and cookies for a paused job.</summary>
public sealed record LinkRefreshed : IpcMessage
{
    public required string JobId { get; init; }

    public required string Url { get; init; }

    public required IReadOnlyList<IpcCookie> Cookies { get; init; }
}

/// <summary>Extension to Kasi: show Kasi's settings window (the options page, TRD 9.1); answered with <see cref="Ack"/>.</summary>
public sealed record OpenSettings : IpcMessage
{
    /// <summary>Which page to show; absent means <see cref="SettingsSection.Browser"/>.</summary>
    public SettingsSection? Section { get; init; }
}

/// <summary>The result for a correlation id (both directions).</summary>
public sealed record Ack : IpcMessage
{
    /// <summary>Always true; a failure is an <see cref="IpcError"/>.</summary>
    public required bool Ok { get; init; }

    public string? Detail { get; init; }
}

/// <summary>A failure for a correlation id (both directions). The detail never carries the offending input.</summary>
public sealed record IpcError : IpcMessage
{
    public required IpcErrorCode Code { get; init; }

    public string? Detail { get; init; }
}
