using Kasi.Ipc.Messages;

namespace Kasi.App.Startup;

/// <summary>
/// What a launch asks for: URLs to add and <c>--tray</c>, nothing else (ADR 0023). The same parser reads this process's arguments and the
/// ones a second launch hands over. Everything else, and any URL that is not http, https or ftp or is too long, is counted and dropped.
/// The URLs may hold secrets: they are never logged, only counted.
/// </summary>
internal sealed record AppArguments(bool Tray, IReadOnlyList<string> Urls, int Rejected)
{
    public const string TrayFlag = "--tray";

    public static AppArguments Parse(IEnumerable<string> args)
    {
        var tray = false;
        var urls = new List<string>();
        var rejected = 0;
        foreach (var arg in args)
        {
            if (string.Equals(arg, TrayFlag, StringComparison.OrdinalIgnoreCase))
            {
                tray = true;
            }
            else if (IsAcceptableUrl(arg))
            {
                urls.Add(arg);
            }
            else
            {
                rejected++;
            }
        }

        return new AppArguments(tray, urls, rejected);
    }

    /// <summary>The arguments to hand to the first instance: only what <see cref="Parse"/> accepted.</summary>
    public IReadOnlyList<string> ToHandOff() => Tray ? [TrayFlag, .. Urls] : Urls;

    private static bool IsAcceptableUrl(string value) =>
        value.Length is > 0 and <= IpcLimits.UrlLength
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" or "ftp"
        && !value.Any(char.IsControl);
}
