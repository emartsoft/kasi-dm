namespace Kasi.Engine.Tests;

public sealed class HttpTransferRunnerTests
{
    [Fact]
    public async Task A_non_positive_write_buffer_age_is_rejected_before_the_probe()
    {
        var act = async () => await new HttpTransferRunner().DownloadAsync(
            new Uri("https://example.com/file.bin"),
            Path.GetTempPath(),
            new HttpTransferOptions { WriteBufferMaxAge = TimeSpan.Zero },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Resume_without_an_ETag_or_Last_Modified_is_refused_before_touching_the_disk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kasi-resume-refused-" + Guid.NewGuid().ToString("N"));

        var outcome = await new HttpTransferRunner().ResumeAsync(
            new Uri("https://example.com/file.bin"),
            folder,
            "file.bin",
            totalBytes: 10,
            eTag: null,
            lastModified: null,
            existingSegments: [],
            cancellationToken: TestContext.Current.CancellationToken);

        var failed = outcome.Should().BeOfType<TransferFailed>().Subject;
        failed.ErrorKind.Should().Be(TransferErrorKind.FileChanged);
        Directory.Exists(folder).Should().BeFalse("the refusal happens before the part file is opened");
    }
}
