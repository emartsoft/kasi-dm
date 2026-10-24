namespace Kasi.Core;

/// <summary>
/// Chooses the folder of a job once the probe has told what the server calls the file (KASI-CAT-001, TRD 10.4 rule 3: the MIME type from the
/// probe, if the extension is missing). The engine asks it for a job marked <see cref="Job.CategoryAuto"/> that has saved no bytes, before
/// the part file exists. The answer is the folder to save in, or null to keep the job's folder; the router saves the job's new category and
/// folder itself, so a resume finds the file where it was put.
/// </summary>
public interface IPostProbeRouter
{
    Task<string?> RouteAsync(Job job, string fileName, string? contentType, CancellationToken cancellationToken = default);
}
