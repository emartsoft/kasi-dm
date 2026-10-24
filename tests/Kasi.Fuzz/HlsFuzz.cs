using System.Text;
using Kasi.Media;

namespace Kasi.Fuzz;

/// <summary>Fuzz target for the HLS master/media parsers (TPL 7.2); every malformed input must become a MediaParseException.</summary>
public static class HlsFuzz
{
    private static readonly Uri _base = new("https://fuzz.invalid/media/master.m3u8");

    public static void Run(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        try
        {
            _ = HlsParser.ParseMaster(text, _base);
        }
        catch (MediaParseException)
        {
        }

        try
        {
            _ = HlsParser.ParseMedia(text, _base);
        }
        catch (MediaParseException)
        {
        }
    }
}
