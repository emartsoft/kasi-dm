using Kasi.Core;

namespace Kasi.App.Engine;

/// <summary>
/// The engine's second look at a job's category, after the probe (KASI-CAT-001, TRD 10.4 rule 3): a download whose link told nothing is
/// routed by the MIME type the server reports. Runs for a job marked <see cref="Job.CategoryAuto"/> that has saved nothing, so a person's
/// choice and a rule's choice are never undone, and the move happens before the part file exists.
/// </summary>
internal sealed class CategoryRouter(
    ICategoryRepository categories,
    ICategoryRuleRepository rules,
    IJobRepository jobs,
    string downloadsFolder) : IPostProbeRouter
{
    public async Task<string?> RouteAsync(Job job, string fileName, string? contentType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!Uri.TryCreate(job.Url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var all = await categories.ListAsync(cancellationToken).ConfigureAwait(false);
        var ruleList = await rules.ListAsync(cancellationToken).ConfigureAwait(false);
        var match = CategoryResolver.Resolve(uri, fileName, contentType, ruleList, all);
        if (match is null || match.IsFallback || match.Category.Id == job.CategoryId)
        {
            return null; // the server told no more than the link did
        }

        var folder = CategoryFolder.For(downloadsFolder, match.Category);
        return await jobs.UpdateRoutingAsync(job.Id, match.Category.Id, folder, cancellationToken).ConfigureAwait(false) ? folder : null;
    }
}

/// <summary>Where a category saves its files: its folder under Downloads, or Downloads itself for General.</summary>
internal static class CategoryFolder
{
    public static string For(string downloadsFolder, Category? category) =>
        string.IsNullOrEmpty(category?.Folder) ? downloadsFolder : Path.Combine(downloadsFolder, category.Folder);
}
