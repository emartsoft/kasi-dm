using Kasi.Ipc.Messages;

namespace Kasi.Ipc;

/// <summary>The checks the JSON shape cannot express: lengths, counts, ranges and URL schemes (the same caps as the schema).</summary>
internal static class IpcValidator
{
    private static readonly string[] _webSchemes = ["http", "https"];
    private static readonly string[] _downloadSchemes = ["http", "https", "ftp", "ftps"];

    public static void Check(IpcMessage message)
    {
        if (!IsValid(message))
        {
            throw new IpcProtocolException(IpcErrorCode.InvalidMessage);
        }
    }

    private static bool IsValid(IpcMessage message) => Text(message.Id, IpcLimits.IdLength, min: 1) && message switch
    {
        Hello m => Text(m.ExtensionVersion, IpcLimits.ShortText, min: 1)
            && m.ProtocolMin is >= 1 and <= IpcLimits.MaxProtocolVersion
            && m.ProtocolMax >= m.ProtocolMin && m.ProtocolMax <= IpcLimits.MaxProtocolVersion,
        Rules m => List(m.Extensions) && List(m.MimeTypes) && List(m.ExcludedSites)
            && m.AckTimeoutMs is >= IpcLimits.AckTimeoutMinMs and <= IpcLimits.AckTimeoutMaxMs,
        AddDownload m => Url(m.Url, _downloadSchemes) && Url(m.FinalUrl, _downloadSchemes) && Optional(m.Referrer, v => Url(v, _webSchemes))
            && Text(m.Filename, IpcLimits.FileNameLength, min: 1) && Text(m.Mime, IpcLimits.MimeLength)
            && Text(m.UserAgent, IpcLimits.UserAgentLength) && Cookies(m.Cookies),
        AddBatch m => Url(m.Referrer, _webSchemes) && Text(m.UserAgent, IpcLimits.UserAgentLength) && Cookies(m.Cookies) && Links(m.Links),
        MediaDetected m => m.TabId >= 0 && Url(m.PageUrl, _webSchemes) && Url(m.Url, _webSchemes)
            && Optional(m.Title, v => Text(v, IpcLimits.TitleLength)),
        CaptureMedia m => m.TabId >= 0 && Url(m.Url, _webSchemes) && Url(m.Referrer, _webSchemes)
            && Text(m.UserAgent, IpcLimits.UserAgentLength) && Cookies(m.Cookies),
        RefreshLink m => Text(m.JobId, IpcLimits.IdLength, min: 1) && Url(m.ReferrerUrl, _webSchemes)
            && Optional(m.MatchHint, v => Text(v, IpcLimits.ListEntryLength, min: 1)),
        LinkRefreshed m => Text(m.JobId, IpcLimits.IdLength, min: 1) && Url(m.Url, _downloadSchemes) && Cookies(m.Cookies),
        OpenSettings => true,
        Ack m => m.Ok && Optional(m.Detail, v => Text(v, IpcLimits.DetailLength)),
        IpcError m => Optional(m.Detail, v => Text(v, IpcLimits.DetailLength)),
        _ => false,
    };

    private static bool Text(string? value, int max, int min = 0) => value is not null && value.Length >= min && value.Length <= max;

    private static bool Optional(string? value, Func<string, bool> check) => value is null || check(value);

    private static bool Url(string? value, string[] schemes) =>
        value is { Length: > 0 and <= IpcLimits.UrlLength }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && Array.IndexOf(schemes, uri.Scheme) >= 0;

    private static bool List(IReadOnlyList<string> values) =>
        values.Count <= IpcLimits.ListEntries && values.All(v => Text(v, IpcLimits.ListEntryLength, min: 1));

    private static bool Cookies(IReadOnlyList<IpcCookie> cookies) =>
        cookies.Count <= IpcLimits.Cookies
        && cookies.All(c => Text(c.Name, IpcLimits.CookieNameLength, min: 1) && Text(c.Value, IpcLimits.CookieValueLength)
            && Text(c.Domain, IpcLimits.CookieDomainLength, min: 1) && Text(c.Path, IpcLimits.CookiePathLength, min: 1));

    private static bool Links(IReadOnlyList<IpcLink> links) =>
        links.Count is >= 1 and <= IpcLimits.BatchLinks
        && links.All(l => Url(l.Url, _downloadSchemes) && Optional(l.Filename, v => Text(v, IpcLimits.FileNameLength, min: 1)));
}
