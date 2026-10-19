namespace Kasi.Engine;

/// <summary>Per-transfer proxy routing. Authentication belongs in <c>JobSecret.ProxyCredentials</c>.</summary>
public sealed record ProxySettings
{
    public ProxyMode Mode { get; init; } = ProxyMode.System;

    /// <summary>HTTP, HTTPS, or SOCKS5 proxy endpoint, with no user information or path.</summary>
    public Uri? Uri { get; init; }

    /// <summary>Exact hosts or leading-dot domain suffixes that connect directly in custom mode.</summary>
    public IReadOnlyList<string> BypassHosts { get; init; } = [];

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (Mode != ProxyMode.Custom)
        {
            if (Uri is not null || BypassHosts.Count > 0)
            {
                throw new ArgumentException("A proxy URI and bypass hosts require custom proxy mode.");
            }

            return;
        }

        if (Uri is null || !Uri.IsAbsoluteUri || Uri.Scheme is not ("http" or "https" or "socks5")
            || string.IsNullOrWhiteSpace(Uri.Host) || Uri.UserInfo.Length > 0 || Uri.AbsolutePath != "/"
            || Uri.Query.Length > 0 || Uri.Fragment.Length > 0)
        {
            throw new ArgumentException("A custom proxy needs an HTTP, HTTPS, or SOCKS5 endpoint without credentials or a path.");
        }

        if (BypassHosts.Any(host => string.IsNullOrWhiteSpace(host)
            || host.Contains('/') || host.Contains(':') || host.Contains('*') || host.Contains('@')
            || host.TrimStart('.').Length == 0))
        {
            throw new ArgumentException("Bypass entries must be host names or leading-dot domain suffixes.");
        }
    }
}
