using System.Security.Cryptography;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-ENG-012")]
[Trait("TC", "TC-ENG-020")]
public sealed class HttpTransferChecksumTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-checksum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        finally
        {
            await _host.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(ChecksumAlgorithm.Md5, "norange", 1_048_576)]
    [InlineData(ChecksumAlgorithm.Sha1, "files", 2_097_152)]
    [InlineData(ChecksumAlgorithm.Sha256, "files", 3_145_728)]
    [InlineData(ChecksumAlgorithm.Sha256, "files", 0)]
    public async Task Matching_checksum_commits_the_verified_file(ChecksumAlgorithm algorithm, string route, int size)
    {
        var seed = $"checksum-{algorithm}-{size}";
        var expected = Hash(GeneratedContent.Bytes(seed, size), algorithm);
        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/{route}/{seed}/{size}"),
            _folder,
            new HttpTransferOptions { ExpectedChecksum = new ExpectedChecksum(algorithm, expected.ToUpperInvariant()) },
            TestContext.Current.CancellationToken);

        var completed = outcome.Should().BeOfType<TransferCompleted>().Subject;
        File.Exists(completed.FinalPath).Should().BeTrue();
        File.Exists(completed.FinalPath + PartFileWriter.PartExtension).Should().BeFalse();
        var finalBytes = await File.ReadAllBytesAsync(completed.FinalPath, TestContext.Current.CancellationToken);
        Convert.ToHexStringLower(SHA256.HashData(finalBytes)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task Wrong_checksum_keeps_the_complete_part_and_does_not_commit()
    {
        const string seed = "checksum-wrong";
        const int size = 2 * 1024 * 1024;
        var outcome = await new HttpTransferRunner().DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions { ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, new string('0', 64)) },
            TestContext.Current.CancellationToken);

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.VerificationFailed);
        failed.Detail.Should().Contain("Checksum mismatch").And.Contain(GeneratedContent.Sha256Hex(seed, size));
        var finalPath = Path.Combine(_folder, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Exists(finalPath).Should().BeFalse();
        var partPath = finalPath + PartFileWriter.PartExtension;
        File.Exists(partPath).Should().BeTrue();
        var partBytes = await File.ReadAllBytesAsync(partPath, TestContext.Current.CancellationToken);
        Convert.ToHexStringLower(SHA256.HashData(partBytes)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task A_retry_after_a_mismatch_is_a_fresh_download_that_completes_with_the_right_checksum()
    {
        const string seed = "checksum-retry";
        const int size = 1_048_576;
        var runner = new HttpTransferRunner();
        var failed = await runner.DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions { ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, new string('0', 64)) },
            TestContext.Current.CancellationToken);
        failed.Should().BeOfType<TransferFailed>().Subject.ErrorKind.Should().Be(TransferErrorKind.VerificationFailed);
        var finalPath = Path.Combine(_folder, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Delete(finalPath + PartFileWriter.PartExtension); // the engine facade deletes the part and resets progress before a retry

        var retried = await runner.DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions { ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, GeneratedContent.Sha256Hex(seed, size)) },
            TestContext.Current.CancellationToken);

        retried.Should().BeOfType<TransferCompleted>();
        Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(finalPath, TestContext.Current.CancellationToken)))
            .Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    [Fact]
    public async Task Overwrite_keeps_the_old_file_until_verification_succeeds()
    {
        const string seed = "checksum-overwrite";
        const int size = 1_048_576;
        var finalPath = Path.Combine(_folder, size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await File.WriteAllBytesAsync(finalPath, [9, 8, 7], TestContext.Current.CancellationToken);
        var runner = new HttpTransferRunner();

        var rejected = await runner.DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions
            {
                OverwriteDestination = true,
                ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, new string('0', 64)),
            },
            TestContext.Current.CancellationToken);

        rejected.Should().BeOfType<TransferFailed>().Subject.ErrorKind.Should().Be(TransferErrorKind.VerificationFailed);
        (await File.ReadAllBytesAsync(finalPath, TestContext.Current.CancellationToken)).Should().Equal((byte)9, 8, 7);

        var accepted = await runner.DownloadAsync(
            Url($"/files/{seed}/{size}"),
            _folder,
            new HttpTransferOptions
            {
                OverwriteDestination = true,
                ExpectedChecksum = new ExpectedChecksum(ChecksumAlgorithm.Sha256, GeneratedContent.Sha256Hex(seed, size)),
            },
            TestContext.Current.CancellationToken);

        var completed = accepted.Should().BeOfType<TransferCompleted>().Subject;
        var bytes = await File.ReadAllBytesAsync(completed.FinalPath, TestContext.Current.CancellationToken);
        Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(GeneratedContent.Sha256Hex(seed, size));
    }

    private Uri Url(string path) => new(_host.BaseAddress, path);

    private static string Hash(byte[] bytes, ChecksumAlgorithm algorithm)
    {
        var hash = algorithm switch
        {
#pragma warning disable CA5351 // Published file checksum verification is not a security primitive.
            ChecksumAlgorithm.Md5 => MD5.HashData(bytes),
#pragma warning restore CA5351
#pragma warning disable CA5350 // Published file checksum verification is not a security primitive.
            ChecksumAlgorithm.Sha1 => SHA1.HashData(bytes),
#pragma warning restore CA5350
            _ => SHA256.HashData(bytes),
        };
        return Convert.ToHexStringLower(hash);
    }
}
