using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kasi.TestServer.Tests;

public class GeneratedContentTests
{
    [Theory]
    [InlineData("a", 0L)]
    [InlineData("a", 31L)]
    [InlineData("a", 32L)]
    [InlineData("a", 4095L)]
    [InlineData("a", 4096L)]
    [InlineData("seed-2", 10_000L)]
    [InlineData("seed-2", 4_294_967_297L)] // 4 GiB + 1
    public void Byte_i_is_the_ith_modulo_32_byte_of_SHA256_of_seed_and_block_index(string seed, long i)
    {
        var input = new byte[Encoding.UTF8.GetByteCount(seed) + 8];
        Encoding.UTF8.GetBytes(seed, input);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(input.Length - 8), i / 4096);
        var expected = SHA256.HashData(input)[(int)(i % 32)];
        var actual = new byte[1];

        GeneratedContent.Fill(seed, i, actual);

        actual[0].Should().Be(expected);
    }

    [Fact]
    public void Reading_in_pieces_gives_the_same_bytes_as_reading_at_once()
    {
        var whole = GeneratedContent.Bytes("s", 20_000);
        var pieces = new byte[20_000];

        GeneratedContent.Fill("s", 0, pieces.AsSpan(0, 5000));
        GeneratedContent.Fill("s", 5000, pieces.AsSpan(5000, 4097));
        GeneratedContent.Fill("s", 9097, pieces.AsSpan(9097));

        pieces.Should().Equal(whole);
    }

    [Fact]
    public void Different_seeds_give_different_files()
    {
        GeneratedContent.Bytes("one", 4096).Should().NotEqual(GeneratedContent.Bytes("two", 4096));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4096L)]
    [InlineData(4097L)]
    [InlineData(200_000L)]
    public void Sha256Hex_matches_hashing_the_materialised_bytes(long size)
    {
        var expected = Convert.ToHexStringLower(SHA256.HashData(GeneratedContent.Bytes("h", size)));

        GeneratedContent.Sha256Hex("h", size).Should().Be(expected);
    }
}
