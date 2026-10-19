using System.Net;

namespace Kasi.Engine;

/// <summary>Small exact-host and domain-suffix proxy router; bypass checks never use URL regexes.</summary>
internal sealed class CustomWebProxy(ProxySettings settings, ICredentials? credentials) : IWebProxy
{
    public ICredentials? Credentials { get; set; } = credentials;

    public Uri GetProxy(Uri destination) => IsBypassed(destination) ? destination : settings.Uri!;

    public bool IsBypassed(Uri host)
    {
        var name = host.IdnHost;
        foreach (var entry in settings.BypassHosts)
        {
            if (entry.StartsWith('.'))
            {
                if (name.EndsWith(entry, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (name.Equals(entry, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
