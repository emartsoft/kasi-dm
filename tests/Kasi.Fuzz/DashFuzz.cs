using System.Text;
using Kasi.Media;

namespace Kasi.Fuzz;

/// <summary>Fuzz target for the MPD parser (TPL 7.2); DTD/XXE, caps and hostile timelines must become MediaParseException.</summary>
public static class DashFuzz
{
    private static readonly Uri _base = new("https://fuzz.invalid/media/manifest.mpd");

    public static void Run(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        try
        {
            _ = DashParser.Parse(text, _base);
        }
        catch (MediaParseException)
        {
        }
    }
}
