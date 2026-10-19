using System.Security.Cryptography;

namespace Kasi.Engine.Tests;

[Trait("Requirement", "KASI-ENG-012")]
[Trait("TC", "TC-ENG-020")]
public sealed class ChecksumVerifierTests
{
    [Theory]
    [InlineData("MD5", ChecksumAlgorithm.Md5, 32)]
    [InlineData("sha-1", ChecksumAlgorithm.Sha1, 40)]
    [InlineData("SHA256", ChecksumAlgorithm.Sha256, 64)]
    public void Published_checksum_is_parsed_and_normalised(string name, ChecksumAlgorithm algorithm, int length)
    {
        var upperHex = new string('A', length);
        ExpectedChecksum.TryParse(name, upperHex, out var parsed).Should().BeTrue();
        parsed.Should().NotBeNull();
        parsed.Algorithm.Should().Be(algorithm);
        parsed.Hex.Should().Be(upperHex.ToLowerInvariant());
    }

    [Theory]
    [InlineData("sha256", "abc")]
    [InlineData("sha256", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("unknown", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Invalid_algorithm_length_or_hex_is_rejected_before_download(string name, string hex)
    {
        ExpectedChecksum.TryParse(name, hex, out var parsed).Should().BeFalse();
        parsed.Should().BeNull();
        var invalid = new HttpTransferOptions { ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, hex) };
        var validate = invalid.Validate;
        validate.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(ChecksumAlgorithm.Md5)]
    [InlineData(ChecksumAlgorithm.Sha1)]
    [InlineData(ChecksumAlgorithm.Sha256)]
    public async Task Streams_file_through_the_selected_algorithm(ChecksumAlgorithm algorithm)
    {
        var path = Path.GetTempFileName();
        try
        {
            var bytes = new byte[PartFileWriter.WriteBufferSize + 77];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var expected = algorithm switch
            {
#pragma warning disable CA5351 // These legacy hashes are compared with a published checksum, not used for security.
                ChecksumAlgorithm.Md5 => MD5.HashData(bytes),
#pragma warning restore CA5351
#pragma warning disable CA5350 // These legacy hashes are compared with a published checksum, not used for security.
                ChecksumAlgorithm.Sha1 => SHA1.HashData(bytes),
#pragma warning restore CA5350
                _ => SHA256.HashData(bytes),
            };

            var actual = await ChecksumVerifier.ComputeAsync(path, algorithm, TestContext.Current.CancellationToken);
            actual.Should().Be(Convert.ToHexStringLower(expected));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_cancelled_token_stops_the_hash_and_leaves_the_file_alone()
    {
        var path = Path.Combine(Path.GetTempPath(), "kasi-checksum-cancel-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, new byte[1_048_576], TestContext.Current.CancellationToken);
        try
        {
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var act = async () => await ChecksumVerifier.ComputeAsync(path, ChecksumAlgorithm.Sha256, cancelled.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            new FileInfo(path).Length.Should().Be(1_048_576);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Cancelled_hash_keeps_the_source_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, new byte[1024], TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await ChecksumVerifier.ComputeAsync(path, ChecksumAlgorithm.Sha256, cancellation.Token));
            File.Exists(path).Should().BeTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
