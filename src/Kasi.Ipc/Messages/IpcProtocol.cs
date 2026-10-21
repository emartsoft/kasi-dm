namespace Kasi.Ipc.Messages;

/// <summary>The protocol version this build speaks (TRD 9.4).</summary>
public static class IpcProtocol
{
    public const int Version = 1;

    /// <summary>The first extension version that accepts <c>minSizeBytes</c> in <c>rules</c>; an older strict parser rejects the whole message.</summary>
    public const string MinSizeExtensionVersion = "0.2.0";
}

/// <summary>
/// The caps every message is held to, so that a hostile peer cannot make the parser hold more than a bounded amount after the 64 MiB
/// frame limit. The same numbers are in <c>docs/protocol/kasi-messages.schema.json</c>; a test keeps the two in step.
/// </summary>
public static class IpcLimits
{
    public const int IdLength = 64;
    public const int ShortText = 64;
    public const int ListEntryLength = 256;
    public const int ListEntries = 1_000;
    public const int UrlLength = 8_192;
    public const int FileNameLength = 255;
    public const int MimeLength = 255;
    public const int UserAgentLength = 1_024;
    public const int TitleLength = 1_024;
    public const int DetailLength = 256;
    public const int Cookies = 200;
    public const int CookieNameLength = 256;
    public const int CookieValueLength = 4_096;
    public const int CookieDomainLength = 255;
    public const int CookiePathLength = 2_048;
    public const int BatchLinks = 100_000;
    public const int AckTimeoutMinMs = 100;
    public const int AckTimeoutMaxMs = 60_000;
    public const long MinSizeMaxBytes = 9_007_199_254_740_991;
    public const int MaxProtocolVersion = 1_000;
}
