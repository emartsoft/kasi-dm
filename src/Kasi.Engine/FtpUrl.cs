using Kasi.Core;

namespace Kasi.Engine;

/// <summary>Moves legacy FTP URL user information into the job secret before persistence.</summary>
public static class FtpUrl
{
    public static (Uri CleanUrl, HttpCredentials? Credentials) StripUserInfo(Uri url)
    {
        ValidateScheme(url);
        if (url.UserInfo.Length == 0)
        {
            return (url, null);
        }

        var split = url.UserInfo.Split(':', 2);
        var credentials = new HttpCredentials
        {
            UserName = Uri.UnescapeDataString(split[0]),
            Password = split.Length == 2 ? Uri.UnescapeDataString(split[1]) : string.Empty,
        };
        var clean = new UriBuilder(url) { UserName = string.Empty, Password = string.Empty }.Uri;
        return (clean, credentials);
    }

    internal static void ValidateScheme(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.Scheme is not ("ftp" or "ftps"))
        {
            throw new ArgumentException("The URL must be an absolute FTP or FTPS URL.", nameof(url));
        }
    }
}
