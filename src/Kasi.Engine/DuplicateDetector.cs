using Kasi.Core;

namespace Kasi.Engine;

/// <summary>
/// Checks the current job list and filesystem before the add-download prompt. The facade must serialise this check with
/// the subsequent add, because another process could otherwise claim the suggested name (KASI-ENG-013, TC-ENG-021).
/// </summary>
public static class DuplicateDetector
{
    private static readonly string[] _multiPartExtensions = [".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"];

    public static async Task<DuplicateCheckResult> CheckAsync(
        IJobRepository jobs,
        Uri url,
        string folder,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!url.IsAbsoluteUri)
        {
            throw new ArgumentException("The job URL must be absolute.", nameof(url));
        }

        var safeName = FileNameSanitiser.Sanitise(fileName);
        var fullFolder = Path.GetFullPath(folder);
        var targetPath = Path.Combine(fullFolder, safeName);
        var partPath = targetPath + PartFileWriter.PartExtension;
        var targetExists = File.Exists(targetPath);
        var partExists = File.Exists(partPath);
        var allJobs = await jobs.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var requestedUrl = Identity(url);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Job? urlMatch = null;
        Job? pathMatch = null;
        foreach (var job in allJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Uri.TryCreate(job.Url, UriKind.Absolute, out var existingUrl)
                && string.Equals(requestedUrl, Identity(existingUrl), StringComparison.Ordinal))
            {
                if (urlMatch is null || IsResumeCandidate(job, requestedUrl))
                {
                    urlMatch = job;
                }
            }

            if (JobPath(job) is { } jobPath && string.Equals(targetPath, jobPath, pathComparer))
            {
                pathMatch ??= job;
            }
        }

        var existing = urlMatch ?? pathMatch;
        var hasConflict = existing is not null || targetExists || partExists;
        if (!hasConflict)
        {
            return new DuplicateCheckResult(null, false, false, safeName, []);
        }

        var actions = new List<DuplicateAction>(3);
        if (urlMatch is not null && IsResumeCandidate(urlMatch, requestedUrl)
            && File.Exists(Path.Combine(urlMatch.Folder, urlMatch.FileName + PartFileWriter.PartExtension)))
        {
            actions.Add(DuplicateAction.Resume);
        }

        actions.Add(DuplicateAction.Overwrite);
        actions.Add(DuplicateAction.AutoRename);
        var suggestion = SuggestAvailableName(fullFolder, safeName, allJobs, pathComparer);
        return new DuplicateCheckResult(existing, targetExists, partExists, suggestion, actions);
    }

    private static string SuggestAvailableName(string folder, string fileName, IReadOnlyList<Job> jobs, StringComparison pathComparer)
    {
        var extension = _multiPartExtensions.FirstOrDefault(value => fileName.EndsWith(value, StringComparison.OrdinalIgnoreCase))
            ?? Path.GetExtension(fileName);
        var stem = fileName[..^extension.Length];
        for (var number = 1; number < int.MaxValue; number++)
        {
            var suffix = $" ({number})";
            var maxStemLength = FileNameSanitiser.MaxLength - suffix.Length - extension.Length;
            if (maxStemLength < 1)
            {
                throw new InvalidOperationException("The filename extension leaves no room for an automatic rename.");
            }

            var stemLength = Math.Min(stem.Length, maxStemLength);
            if (stemLength < stem.Length && char.IsHighSurrogate(stem[stemLength - 1]) && char.IsLowSurrogate(stem[stemLength]))
            {
                stemLength--;
            }

            var candidate = stem[..stemLength] + suffix + extension;
            var candidatePath = Path.Combine(folder, candidate);
            if (!File.Exists(candidatePath)
                && !Directory.Exists(candidatePath)
                && !File.Exists(candidatePath + PartFileWriter.PartExtension)
                && !jobs.Any(job => string.Equals(candidatePath, JobPath(job), pathComparer)))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("No filename is available for automatic rename.");
    }

    private static bool IsResumeCandidate(Job job, string requestedUrl) =>
        job.Kind is JobKind.Http or JobKind.Ftp
        && job.Resumable
        && job.State is not (JobState.Completed or JobState.Failed)
        && Uri.TryCreate(job.Url, UriKind.Absolute, out var existingUrl)
        && string.Equals(requestedUrl, Identity(existingUrl), StringComparison.Ordinal);

    // Scheme, host and non-default port plus path and query: user information and the fragment do not identify a file.
    private static string Identity(Uri url) =>
        $"{url.Scheme}://{url.IdnHost}{(url.IsDefaultPort ? string.Empty : ":" + url.Port.ToString(System.Globalization.CultureInfo.InvariantCulture))}{url.PathAndQuery}";

    // A stored job whose folder or name is not a usable path cannot collide with anything; it must not fail the whole check.
    private static string? JobPath(Job job)
    {
        try
        {
            return Path.GetFullPath(Path.Combine(job.Folder, job.FileName));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
