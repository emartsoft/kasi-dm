using System.Text;

namespace Kasi.Engine;

/// <summary>
/// The Mark-of-the-Web (KASI-CAT-003, TRD 6.4): the <c>Zone.Identifier</c> stream that tells Windows, SmartScreen and Office a file came from
/// the Internet. Written on the part file after it is sealed and before it is renamed; the stream travels with the rename. It carries
/// <c>ZoneId=3</c> and the address the file came from <b>without user name, password, query or fragment</b>. There is no switch: this is a
/// safety property, like the DRM refusal.
/// </summary>
public static class MarkOfTheWeb
{
    public const string StreamName = "Zone.Identifier";

    /// <summary>The text of the stream. <paramref name="source"/> may be null (the address is then left out).</summary>
    public static string Content(Uri? source)
    {
        var text = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
        if (source is { IsAbsoluteUri: true } && (source.Scheme == Uri.UriSchemeHttp || source.Scheme == Uri.UriSchemeHttps))
        {
            var bare = new UriBuilder(source) { Query = string.Empty, Fragment = string.Empty, UserName = string.Empty, Password = string.Empty };
            text.Append("HostUrl=").Append(bare.Uri.GetLeftPart(UriPartial.Path)).Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>Writes the stream on <paramref name="path"/>. False when the volume cannot hold streams (FAT32, exFAT, some shares); the download goes on.</summary>
    public static bool TryWrite(string path, Uri? source)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true; // no such mark on this system; nothing to report
        }

        try
        {
            File.WriteAllText(path + ":" + StreamName, Content(source), Encoding.ASCII);
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }
}
