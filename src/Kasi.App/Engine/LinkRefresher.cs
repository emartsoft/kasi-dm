using Kasi.App.Browser;
using Kasi.Core;

namespace Kasi.App.Engine;

internal enum RefreshOutcome
{
    Refreshed,
    NotFound,
    NoReferrer,
    NoBrowser,
    TimedOut,
    Rejected,
    Failed,
}

internal interface ILinkRefresher
{
    Task<RefreshOutcome> RefreshAsync(JobId id, CancellationToken cancellationToken);
}

/// <summary>
/// Gets a fresh link for a paused job (TRD 6.6): asks the connected browsers to open the page the download came from, takes the link and
/// cookies of the download that page starts, and gives them to the engine, which checks the link serves the same file before resuming
/// from the saved bytes. Never logs or shows a URL.
/// </summary>
internal sealed class LinkRefresher(IJobService jobs, IRefreshBroker broker) : ILinkRefresher
{
    public async Task<RefreshOutcome> RefreshAsync(JobId id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return RefreshOutcome.NotFound;
        }

        if (string.IsNullOrEmpty(job.Referrer))
        {
            return RefreshOutcome.NoReferrer; // a link typed in has no page to go back to
        }

        var result = await broker.RequestAsync(id.ToString(), job.Referrer, job.FileName, cancellationToken).ConfigureAwait(false);
        if (result.Answer is not { } answer)
        {
            return result.Status == RefreshStatus.NoBrowser ? RefreshOutcome.NoBrowser : RefreshOutcome.TimedOut;
        }

        try
        {
            var cookies = answer.Cookies
                .Select(c => new JobCookie { Name = c.Name, Value = c.Value, Domain = c.Domain, Path = c.Path, Secure = c.Secure, HttpOnly = c.HttpOnly })
                .ToList();
            var refreshed = await jobs.RefreshLinkAsync(id, answer.Url, cookies, cancellationToken).ConfigureAwait(false);
            return refreshed.Problem switch
            {
                RefreshProblem.None => RefreshOutcome.Refreshed,
                RefreshProblem.Rejected => RefreshOutcome.Rejected,
                _ => RefreshOutcome.Failed,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            return RefreshOutcome.Failed;
        }
    }
}
