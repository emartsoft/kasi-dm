namespace Kasi.Engine.Tests;

internal static class DownloadEngineTestExtensions
{
    /// <summary>Starts the job and waits until it is no longer running, which is what most tests want to assert on.</summary>
    public static async Task StartAndWaitAsync(this DownloadEngine engine, Kasi.Core.JobId id, CancellationToken cancellationToken)
    {
        await engine.StartJobAsync(id, cancellationToken);
        await engine.WaitForJobAsync(id, cancellationToken);
    }

    public static async Task ResumeAndWaitAsync(this DownloadEngine engine, Kasi.Core.JobId id, CancellationToken cancellationToken)
    {
        await engine.ResumeJobAsync(id, cancellationToken);
        await engine.WaitForJobAsync(id, cancellationToken);
    }
}
