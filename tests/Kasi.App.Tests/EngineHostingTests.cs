using Kasi.App.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-SYS-005")]
public class EngineHostingTests
{
    [Fact]
    public async Task Starting_the_host_migrates_the_database_and_starts_the_engine_so_a_job_can_be_added()
    {
        var ct = TestContext.Current.CancellationToken;
        var data = Path.Combine(Path.GetTempPath(), "kasi-hosting-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var host = CompositionRoot.Build([], data);
            File.Exists(Path.Combine(data, "kasi.db")).Should().BeFalse("building the container must not touch the disk");

            await host.StartAsync(ct);
            var jobs = host.Services.GetRequiredService<IJobService>();
            var added = await jobs.AddAsync(new NewDownload("https://files.example.org/a.zip", "a.zip", QueueId: 1, StartNow: false), cancellationToken: ct);

            added.Job.Should().NotBeNull();
            File.Exists(Path.Combine(data, "kasi.db")).Should().BeTrue();
            await host.StopAsync(ct);
        }
        finally
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // SQLite may still hold a file for a moment; the folder is in the temp directory
            }
        }
    }
}
