namespace Kasi.Media;

/// <summary>A byte range in an HTTP resource, used by HLS byte-range segments and DASH range attributes.</summary>
public sealed record MediaByteRange(long Offset, long Length)
{
    public long EndInclusive => checked(Offset + Length - 1);

    public override string ToString() => $"{Offset}-{EndInclusive}";
}
