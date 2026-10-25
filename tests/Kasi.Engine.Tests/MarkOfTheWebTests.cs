using Kasi.TestServer;

namespace Kasi.Engine.Tests;

/// <summary>11d (docs/plans/m2-11.md): the Mark-of-the-Web written on every completed file. KASI-CAT-003, TC-CAT-003.</summary>
[Trait("Requirement", "KASI-CAT-003")]
[Trait("TC", "TC-CAT-003")]
public sealed class MarkOfTheWebTests : IAsyncLifetime
{
    private TestServerHost _host = null!;
    private string _folder = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestServerHost.StartAsync(cancellationToken: TestContext.Current.CancellationToken);
        _folder = Path.Combine(Path.GetTempPath(), "kasi-motw-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void The_stream_says_zone_3_and_the_address_without_its_credentials_query_and_fragment()
    {
        var text = MarkOfTheWeb.Content(new Uri("https://user:secret@files.example.org:8443/dl/pack.zip?token=SECRET&x=1#frag"));

        text.Should().Be("[ZoneTransfer]\r\nZoneId=3\r\nHostUrl=https://files.example.org:8443/dl/pack.zip\r\n");
        text.Should().NotContain("SECRET").And.NotContain("user");
        MarkOfTheWeb.Content(null).Should().Be("[ZoneTransfer]\r\nZoneId=3\r\n", "a mark without an address is still a mark");
    }

    [Fact]
    public void A_stream_that_cannot_be_written_reports_false_and_does_not_throw()
    {
        MarkOfTheWeb.TryWrite(Path.Combine(_folder, "no-such-folder", "file.part"), new Uri("https://example.org/a")).Should().BeFalse();
    }

    [Fact]
    public async Task A_stream_written_on_a_file_survives_the_rename()
    {
        var part = Path.Combine(_folder, "a.kasipart");
        await File.WriteAllTextAsync(part, "data", TestContext.Current.CancellationToken);

        MarkOfTheWeb.TryWrite(part, new Uri("https://example.org/a?q=1")).Should().BeTrue();
        var final = Path.Combine(_folder, "a.bin");
        File.Move(part, final);

        (await File.ReadAllTextAsync(final + ":Zone.Identifier", TestContext.Current.CancellationToken)).Should().Contain("ZoneId=3").And.Contain("HostUrl=https://example.org/a");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task On_a_volume_that_cannot_hold_the_mark_the_download_completes_and_the_log_says_so()
    {
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var url = new Uri(_host.BaseAddress, "/files/motw-nostream/1048576");

        var outcome = await new HttpTransferRunner().DownloadAsync(
            url, _folder, new HttpTransferOptions { Log = log.Enqueue, MarkWriter = (_, _) => false }, TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<TransferCompleted>();
        log.Should().Contain("Mark-of-the-Web not supported on this volume");
        Directory.GetFiles(_folder).Should().ContainSingle(f => !f.EndsWith(".kasipart", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("files", 3_145_728)]
    [InlineData("norange", 1_048_576)]
    [InlineData("files", 0)]
    [Trait("Category", "Integration")]
    public async Task Every_completed_download_carries_the_mark_whatever_the_transfer_path(string route, int size)
    {
        var seed = $"motw-{route}-{size}";
        var url = new Uri(_host.BaseAddress, $"/{route}/{seed}/{size}?token=SECRET");

        var outcome = await new HttpTransferRunner().DownloadAsync(url, _folder, new HttpTransferOptions(), TestContext.Current.CancellationToken);

        outcome.Should().BeOfType<TransferCompleted>();
        var final = Directory.GetFiles(_folder).Single(f => !f.EndsWith(".kasipart", StringComparison.Ordinal));
        var mark = await File.ReadAllTextAsync(final + ":Zone.Identifier", TestContext.Current.CancellationToken);
        mark.Should().Contain("ZoneId=3").And.Contain("HostUrl=http://127.0.0.1").And.NotContain("SECRET").And.NotContain("token");
    }
}
