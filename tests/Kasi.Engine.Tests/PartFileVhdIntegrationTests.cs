using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using Kasi.TestServer;

namespace Kasi.Engine.Tests;

[Trait("Category", "Integration")]
[Trait("TC", "TC-ENG-015")]
[Trait("Requirement", "KASI-ENG-008")]
[Trait("Requirement", "KASI-RES-001")]
public sealed class PartFileVhdIntegrationTests : IAsyncLifetime
{
    private const int VhdSizeMiB = 64;
    private const long TotalDownloadBytes = 20L * 1024 * 1024;
    private const long RemainingFreeTargetBytes = 10L * 1024 * 1024;
    private const int ChunkBytes = PartFileWriter.WriteBufferSize;
    private const string ContentSeed = "vhd-seed";
    private const string FileName = "payload-20mib.bin";

    private string? _workDir;
    private string? _vhdxPath;
    private string? _mountDir;
    private bool _mounted;

    public ValueTask InitializeAsync()
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_mounted && _vhdxPath is not null)
        {
            try
            {
                await RunDiskPartAsync(
                    [
                        $"select vdisk file=\"{_vhdxPath}\"",
                        "detach vdisk noerr"
                    ],
                    CancellationToken.None);
            }
            catch
            {
                // Best-effort VHD cleanup on test teardown.
            }
        }

        if (_workDir is not null && Directory.Exists(_workDir))
        {
            try
            {
                Directory.Delete(_workDir, recursive: true);
            }
            catch
            {
                // Best-effort temp directory cleanup.
            }
        }
    }

    [Fact]
    public async Task WriteAsync_WhenVhdFillsMidDownload_ReturnsNotEnoughSpaceAndResumesCleanlyAfterSpaceIsFreed()
    {
        var ct = TestContext.Current.CancellationToken;
        var requireVhd = string.Equals(
            Environment.GetEnvironmentVariable("KASI_REQUIRE_VHD"),
            "1",
            StringComparison.Ordinal);

        if (!IsAdministrator())
        {
            if (requireVhd)
            {
                Assert.Fail("KASI_REQUIRE_VHD=1 is set, but the test process is not running with Administrator privileges.");
            }

            Assert.Skip("Requires Administrator privileges to create and mount a temporary VHDX via diskpart.");
            return;
        }

        _workDir = Path.Combine(Path.GetTempPath(), $"kasi-vhd-test-{Guid.NewGuid():N}");
        _vhdxPath = Path.Combine(_workDir, "diskfull.vhdx");
        _mountDir = Path.Combine(_workDir, "mount");
        Directory.CreateDirectory(_mountDir);

        var (exitCode, output) = await RunDiskPartAsync(
            [
                $"create vdisk file=\"{_vhdxPath}\" maximum={VhdSizeMiB} type=fixed",
                $"select vdisk file=\"{_vhdxPath}\"",
                "attach vdisk",
                "create partition primary",
                "format fs=ntfs label=\"KasiVhd\" quick",
                $"assign mount=\"{_mountDir}\""
            ],
            ct);

        if (exitCode != 0)
        {
            if (requireVhd)
            {
                Assert.Fail($"diskpart failed with exit code {exitCode} while KASI_REQUIRE_VHD=1:\n{output}");
            }

            Assert.Skip($"diskpart could not mount temporary VHDX (exit code {exitCode}).");
            return;
        }

        _mounted = true;

        var finalPath = Path.Combine(_mountDir, FileName);
        var partPath = finalPath + PartFileWriter.PartExtension;
        var ballastPath = Path.Combine(_mountDir, "ballast.bin");
        var expectedPayload = GeneratedContent.Bytes(ContentSeed, (int)TotalDownloadBytes);

        // 1. Open the 20 MiB download while the 64 MiB VHD has plenty of free space.
        var opened = PartFileWriter.TryOpen(_mountDir, FileName, TotalDownloadBytes)
            .Should().BeOfType<PartFileOpened>().Subject;
        var writer = opened.Writer;

        long lastFlushedOffset = 0;
        var sawNotEnoughSpace = false;

        try
        {
            // 2. Fill the mounted VHD with a ballast file so ~10 MiB of free space remains.
            WindowsDiskSpaceProvider.Instance
                .TryGetAvailableFreeBytes(_mountDir, out var freeBytesBeforeBallast)
                .Should().BeTrue();

            var ballastBytes = freeBytesBeforeBallast - RemainingFreeTargetBytes;
            ballastBytes.Should().BeGreaterThan(0);
            WritePhysicalFile(ballastPath, ballastBytes);

            // 3. Write 256 KiB chunks sequentially until the VHD runs out of space.
            for (long offset = 0; offset < TotalDownloadBytes; offset += ChunkBytes)
            {
                var length = (int)Math.Min(ChunkBytes, TotalDownloadBytes - offset);
                var slice = expectedPayload.AsMemory((int)offset, length);

                var writeStatus = await writer.WriteAsync(slice, offset, ct);
                if (!writeStatus.IsSuccess)
                {
                    writeStatus.ErrorKind.Should().Be(DiskErrorKind.NotEnoughSpace);
                    sawNotEnoughSpace = true;
                    break;
                }

                lastFlushedOffset = offset + length;
            }

            sawNotEnoughSpace.Should().BeTrue("the 20 MiB write should exhaust the ~10 MiB remaining VHD capacity");

            // 4. Failure must occur between 40% (8 MiB) and 70% (14 MiB) of the 20 MiB download.
            lastFlushedOffset.Should().BeInRange(
                (long)(TotalDownloadBytes * 0.40),
                (long)(TotalDownloadBytes * 0.70));

            _ = await writer.FlushAsync(ct);
        }
        finally
        {
            await writer.DisposeAsync();
        }

        File.Exists(partPath).Should().BeTrue("Dispose without Abandon preserves the .kasipart file for resume");
        File.Exists(finalPath).Should().BeFalse();

        // 5. Free disk space by deleting the ballast file, verify the already-written prefix, then resume from lastFlushedOffset.
        File.Delete(ballastPath);

        var writtenPrefix = new byte[lastFlushedOffset];
        await using (var partReadStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            await partReadStream.ReadExactlyAsync(writtenPrefix, ct);
        }

        writtenPrefix.AsSpan().SequenceEqual(expectedPayload.AsSpan(0, (int)lastFlushedOffset)).Should().BeTrue();

        using (var resumed = PartFileWriter.TryOpen(
            _mountDir,
            FileName,
            TotalDownloadBytes,
            resume: true,
            alreadyWrittenBytes: lastFlushedOffset).Should().BeOfType<PartFileOpened>().Subject)
        {
            var resumedWriter = resumed.Writer;

            for (long offset = lastFlushedOffset; offset < TotalDownloadBytes; offset += ChunkBytes)
            {
                var length = (int)Math.Min(ChunkBytes, TotalDownloadBytes - offset);
                var slice = expectedPayload.AsMemory((int)offset, length);

                var writeStatus = await resumedWriter.WriteAsync(slice, offset, ct);
                writeStatus.IsSuccess.Should().BeTrue(writeStatus.Detail);
            }

            var sealStatus = await resumedWriter.SealAsync(
                expectedLength: TotalDownloadBytes,
                bytesWritten: TotalDownloadBytes,
                ct);
            sealStatus.IsSuccess.Should().BeTrue(sealStatus.Detail);

            var commitStatus = await resumedWriter.CommitAsync(overwriteDestination: false, ct);
            commitStatus.IsSuccess.Should().BeTrue(commitStatus.Detail);
        }

        File.Exists(partPath).Should().BeFalse();
        File.Exists(finalPath).Should().BeTrue();

        var actualBytes = await File.ReadAllBytesAsync(finalPath, ct);
        actualBytes.Length.Should().Be((int)TotalDownloadBytes);
        var actualSha256 = Convert.ToHexStringLower(SHA256.HashData(actualBytes));
        actualSha256.Should().Be(GeneratedContent.Sha256Hex(ContentSeed, (int)TotalDownloadBytes));
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void WritePhysicalFile(string path, long totalBytes)
    {
        using var handle = File.OpenHandle(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            FileOptions.None,
            preallocationSize: totalBytes);

        var chunk = new byte[ChunkBytes];
        chunk.AsSpan().Fill(0xA5);

        long offset = 0;
        while (offset < totalBytes)
        {
            var toWrite = (int)Math.Min(chunk.Length, totalBytes - offset);
            RandomAccess.Write(handle, chunk.AsSpan(0, toWrite), offset);
            offset += toWrite;
        }

        RandomAccess.FlushToDisk(handle);
    }

    private static async Task<(int ExitCode, string Output)> RunDiskPartAsync(
        IReadOnlyList<string> commands,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"kasi-diskpart-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllLinesAsync(scriptPath, commands, cancellationToken);

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "diskpart.exe",
                    Arguments = $"/s \"{scriptPath}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.ExitCode, $"{stdout}\n{stderr}".Trim());
        }
        finally
        {
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
            catch
            {
                // Best-effort script cleanup.
            }
        }
    }
}
