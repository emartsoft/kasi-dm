using System.Collections.Concurrent;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.App.Browser;

internal enum RefreshStatus
{
    Refreshed,
    NoBrowser,
    TimedOut,
}

/// <summary>The fresh link a browser found. Holds cookie values, so it prints only its type.</summary>
internal sealed class RefreshAnswer(string url, IReadOnlyList<IpcCookie> cookies)
{
    public string Url { get; } = url;

    public IReadOnlyList<IpcCookie> Cookies { get; } = cookies;

    public override string ToString() => nameof(RefreshAnswer);
}

internal sealed record RefreshResult(RefreshStatus Status, RefreshAnswer? Answer = null);

/// <summary>Where <c>refreshLink</c> goes out: every connected host. Returns how many took it.</summary>
internal interface IRefreshChannel
{
    Task<int> SendAsync(RefreshLink message, CancellationToken cancellationToken);
}

internal interface IRefreshBroker
{
    /// <summary>Asks the connected browsers to open the page and report the download it starts; waits for the answer.</summary>
    Task<RefreshResult> RequestAsync(string jobId, string referrerUrl, string? matchHint, CancellationToken cancellationToken);

    /// <summary>Hands an extension's <c>linkRefreshed</c> to the request waiting for it; false when nobody asked.</summary>
    bool TryComplete(LinkRefreshed message);
}

/// <summary>
/// Matches the extension's <c>linkRefreshed</c> to the <c>refreshLink</c> Kasi sent for the job (TRD 6.6). The request goes to every
/// connected host, so with two browsers open both open the page; the first answer wins and a late one is refused. The extension gives up
/// after 60 s, so waiting a little longer than that is a timeout, not a hang. Nothing here logs a URL or a cookie.
/// </summary>
internal sealed class RefreshBroker(TimeProvider? time = null, TimeSpan? timeout = null) : IRefreshBroker
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(70);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<LinkRefreshed>> _waiting = new(StringComparer.Ordinal);
    private volatile IRefreshChannel? _channel;

    /// <summary>The pipe to send on; set once the server exists. Until then every request is "no browser".</summary>
    public void Bind(IRefreshChannel channel) => _channel = channel;

    public async Task<RefreshResult> RequestAsync(string jobId, string referrerUrl, string? matchHint, CancellationToken cancellationToken)
    {
        var channel = _channel;
        if (channel is null)
        {
            return new RefreshResult(RefreshStatus.NoBrowser);
        }

        var source = new TaskCompletionSource<LinkRefreshed>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[jobId] = source; // a newer request for the same job replaces an older one
        try
        {
            var message = new RefreshLink { Id = Guid.NewGuid().ToString("N"), JobId = jobId, ReferrerUrl = referrerUrl, MatchHint = matchHint };
            if (await channel.SendAsync(message, cancellationToken).ConfigureAwait(false) == 0)
            {
                return new RefreshResult(RefreshStatus.NoBrowser);
            }

            try
            {
                var answer = await source.Task.WaitAsync(_timeout, _time, cancellationToken).ConfigureAwait(false);
                return new RefreshResult(RefreshStatus.Refreshed, new RefreshAnswer(answer.Url, answer.Cookies));
            }
            catch (TimeoutException)
            {
                return new RefreshResult(RefreshStatus.TimedOut);
            }
        }
        finally
        {
            _waiting.TryRemove(new KeyValuePair<string, TaskCompletionSource<LinkRefreshed>>(jobId, source));
        }
    }

    public bool TryComplete(LinkRefreshed message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _waiting.TryRemove(message.JobId, out var source) && source.TrySetResult(message);
    }
}

/// <summary>The real channel: a broadcast on the browser pipe.</summary>
internal sealed class PipeRefreshChannel(KasiPipeServer server) : IRefreshChannel
{
    public Task<int> SendAsync(RefreshLink message, CancellationToken cancellationToken) => server.BroadcastAsync(message, cancellationToken);
}
