using System.Text.Json.Serialization;

namespace Kasi.Ipc.Messages;

/// <summary>Enums travel as their exact names; numbers and other spellings are rejected.</summary>
internal sealed class StrictEnumConverter<T>() : JsonStringEnumConverter<T>(namingPolicy: null, allowIntegerValues: false)
    where T : struct, Enum;

[JsonConverter(typeof(StrictEnumConverter<BrowserClient>))]
public enum BrowserClient
{
    [JsonStringEnumMemberName("chrome")] Chrome,
    [JsonStringEnumMemberName("edge")] Edge,
    [JsonStringEnumMemberName("firefox")] Firefox,
}

[JsonConverter(typeof(StrictEnumConverter<BypassKey>))]
public enum BypassKey
{
    [JsonStringEnumMemberName("alt")] Alt,
    [JsonStringEnumMemberName("ctrl")] Ctrl,
    [JsonStringEnumMemberName("shift")] Shift,
    [JsonStringEnumMemberName("none")] None,
}

[JsonConverter(typeof(StrictEnumConverter<MediaKind>))]
public enum MediaKind
{
    [JsonStringEnumMemberName("hls")] Hls,
    [JsonStringEnumMemberName("dash")] Dash,
    [JsonStringEnumMemberName("direct")] Direct,
}

[JsonConverter(typeof(StrictEnumConverter<SettingsSection>))]
public enum SettingsSection
{
    [JsonStringEnumMemberName("general")] General,
    [JsonStringEnumMemberName("browser")] Browser,
}

[JsonConverter(typeof(StrictEnumConverter<IpcErrorCode>))]
public enum IpcErrorCode
{
    [JsonStringEnumMemberName("INVALID_MESSAGE")] InvalidMessage,
    [JsonStringEnumMemberName("UNSUPPORTED_VERSION")] UnsupportedVersion,
    [JsonStringEnumMemberName("APP_NOT_RUNNING")] AppNotRunning,
    [JsonStringEnumMemberName("APP_NOT_INSTALLED")] AppNotInstalled,
    [JsonStringEnumMemberName("INTERNAL")] Internal,
}
