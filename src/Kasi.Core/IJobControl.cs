namespace Kasi.Core;

/// <summary>Starts and pauses individual downloads, implemented by <c>DownloadEngine</c>.</summary>
public interface IJobControl
{
    Task StartJobAsync(JobId id, CancellationToken cancellationToken = default);

    Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default);
}
