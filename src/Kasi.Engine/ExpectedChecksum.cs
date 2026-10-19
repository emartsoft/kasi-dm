namespace Kasi.Engine;

/// <summary>A published checksum to compare with the sealed part file before commit.</summary>
public sealed record ExpectedChecksum(ChecksumAlgorithm Algorithm, string Hex)
{
    public static bool TryParse(string? algorithm, string? hex, out ExpectedChecksum? expected)
    {
        expected = null;
        var parsed = algorithm?.Trim().ToLowerInvariant() switch
        {
            "md5" => ChecksumAlgorithm.Md5,
            "sha1" or "sha-1" => ChecksumAlgorithm.Sha1,
            "sha256" or "sha-256" => ChecksumAlgorithm.Sha256,
            _ => (ChecksumAlgorithm?)null,
        };

        if (parsed is null || !IsValidHex(hex, parsed.Value))
        {
            return false;
        }

        expected = new ExpectedChecksum(parsed.Value, hex!.ToLowerInvariant());
        return true;
    }

    internal void Validate()
    {
        if (!IsValidHex(Hex, Algorithm))
        {
            throw new ArgumentException("The expected checksum must have the correct hexadecimal length for its algorithm.", nameof(Hex));
        }
    }

    private static bool IsValidHex(string? hex, ChecksumAlgorithm algorithm)
    {
        var length = algorithm switch
        {
            ChecksumAlgorithm.Md5 => 32,
            ChecksumAlgorithm.Sha1 => 40,
            ChecksumAlgorithm.Sha256 => 64,
            _ => 0,
        };

        if (hex?.Length != length || length == 0)
        {
            return false;
        }

        foreach (var digit in hex)
        {
            if (!Uri.IsHexDigit(digit))
            {
                return false;
            }
        }

        return true;
    }
}
