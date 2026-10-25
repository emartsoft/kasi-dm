using System.Collections.Concurrent;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.BrowserHarness;

/// <summary>What the stub received for one <c>addDownload</c>. Cookie values are the tests' canaries, never real secrets.</summary>
internal sealed record ReceivedDownload(string Url, string FinalUrl, string? Referrer, string Filename, string Mime, IReadOnlyList<ReceivedCookie> Cookies);

internal sealed record ReceivedCookie(string Name, string Value, string Domain);

/// <summary>What the stub received for one <c>addBatch</c> (the link picker's input, TC-BRW-005).</summary>
internal sealed record ReceivedBatch(string Referrer, IReadOnlyList<string> Urls, IReadOnlyList<ReceivedCookie> Cookies);

/// <summary>What the stub received for one <c>linkRefreshed</c> (the answer to a <c>refreshLink</c>, M2.5c).</summary>
internal sealed record ReceivedRefresh(string JobId, string Url, IReadOnlyList<ReceivedCookie> Cookies);

/// <summary>A mediaDetected or captureMedia message the stub received (KASI-MED-001).</summary>
internal sealed record ReceivedMedia(string Type, string Kind, string Url, string? Referrer, string? Origin, IReadOnlyList<ReceivedCookie> Cookies);

/// <summary>
/// Kasi as the browser protocol sees it: the real <see cref="KasiPipeServer"/> on the real per-user pipe (so the framing, the access list
/// and the native host are all genuine), with a handler that answers <c>hello</c> with rules and <c>addDownload</c> as the mode says.
/// </summary>
internal sealed class StubKasi(HarnessOptions options) : IAsyncDisposable
{
    private readonly ConcurrentQueue<ReceivedDownload> _received = new();
    private readonly ConcurrentQueue<ReceivedBatch> _batches = new();
    private readonly ConcurrentQueue<ReceivedRefresh> _refreshed = new();
    private readonly ConcurrentQueue<ReceivedMedia> _media = new();
    private volatile string _mode = options.Mode;
    private volatile RulesOptions _rules = options.Rules;
    private int _answerDelayMs = options.AnswerDelayMs;
    private int _hellos;
    private KasiPipeServer? _server;

    public IReadOnlyList<ReceivedDownload> Received => [.. _received];

    /// <summary>How many <c>hello</c> messages reached Kasi through a native host (TC-BRW-007: a refused extension sends none).</summary>
    public int Hellos => Volatile.Read(ref _hellos);

    public bool Running => _server is not null;

    public IReadOnlyList<ReceivedBatch> Batches => [.. _batches];

    public IReadOnlyList<ReceivedRefresh> Refreshed => [.. _refreshed];

    public IReadOnlyList<ReceivedMedia> Media => [.. _media];

    public void Clear()
    {
        _refreshed.Clear();
        _received.Clear();
        _batches.Clear();
    }

    /// <summary>Throws when something (a real Kasi, another harness) already holds the pipe: the test must not run against it.</summary>
    public void Start()
    {
        _server = new KasiPipeServer(KasiPipeNames.ForCurrentUser(), HandleAsync);
        _server.Start();
    }

    /// <summary>Closes the pipe as a quitting Kasi would: every connection ends and the pipe name is free (TC-BRW-002, status after Kasi quits).</summary>
    public async Task StopAsync()
    {
        var server = Interlocked.Exchange(ref _server, null);
        if (server is not null)
        {
            await server.DisposeAsync();
        }
    }

    public void SetMode(string mode, int answerDelayMs)
    {
        _mode = mode;
        Volatile.Write(ref _answerDelayMs, answerDelayMs);
    }

    public async Task SetRulesAsync(RulesOptions rules)
    {
        _rules = rules;
        if (_server is not null)
        {
            await _server.BroadcastAsync(rules.ToMessage(Guid.NewGuid().ToString("N")), CancellationToken.None);
        }
    }

    /// <summary>Asks the connected extension for a fresh link, as Kasi's refresh dialog will (M2.8d).</summary>
    public async Task SendRefreshAsync(string jobId, string referrerUrl, string? matchHint)
    {
        if (_server is not null)
        {
            await _server.BroadcastAsync(
                new RefreshLink { Id = Guid.NewGuid().ToString("N"), JobId = jobId, ReferrerUrl = referrerUrl, MatchHint = matchHint },
                CancellationToken.None);
        }
    }

    public ValueTask DisposeAsync() => _server?.DisposeAsync() ?? ValueTask.CompletedTask;

    private static string KindText(MediaKind kind) => kind switch
    {
        MediaKind.Hls => "hls",
        MediaKind.Dash => "dash",
        _ => "direct",
    };

    private async ValueTask<IpcMessage?> HandleAsync(IpcConnection connection, IpcMessage message, CancellationToken cancellationToken)
    {
        switch (message)
        {
            case Hello:
                Interlocked.Increment(ref _hellos);
                return _rules.ToMessage(message.Id);
            case AddDownload add:
                _received.Enqueue(new ReceivedDownload(
                    add.Url,
                    add.FinalUrl,
                    add.Referrer,
                    add.Filename,
                    add.Mime,
                    [.. add.Cookies.Select(c => new ReceivedCookie(c.Name, c.Value, c.Domain))]));
                switch (_mode)
                {
                    case "silent":
                        return null;
                    case "error":
                        return new IpcError { Id = message.Id, Code = IpcErrorCode.Internal };
                    default:
                        await Task.Delay(Volatile.Read(ref _answerDelayMs), cancellationToken);
                        return new Ack { Id = message.Id, Ok = true };
                }

            case AddBatch batch:
                _batches.Enqueue(new ReceivedBatch(
                    batch.Referrer,
                    [.. batch.Links.Select(l => l.Url)],
                    [.. batch.Cookies.Select(c => new ReceivedCookie(c.Name, c.Value, c.Domain))]));
                return new Ack { Id = message.Id, Ok = true };
            case MediaDetected detected:
                _media.Enqueue(new ReceivedMedia("mediaDetected", KindText(detected.Kind), detected.Url, detected.PageUrl, null, []));
                return new Ack { Id = message.Id, Ok = true };
            case CaptureMedia capture:
                _media.Enqueue(new ReceivedMedia(
                    "captureMedia",
                    KindText(capture.Kind),
                    capture.Url,
                    capture.Referrer,
                    capture.Origin,
                    [.. capture.Cookies.Select(c => new ReceivedCookie(c.Name, c.Value, c.Domain))]));
                return new Ack { Id = message.Id, Ok = true };
            case LinkRefreshed refreshed:
                _refreshed.Enqueue(new ReceivedRefresh(
                    refreshed.JobId,
                    refreshed.Url,
                    [.. refreshed.Cookies.Select(c => new ReceivedCookie(c.Name, c.Value, c.Domain))]));
                return new Ack { Id = message.Id, Ok = true };
            default:
                return new Ack { Id = message.Id, Ok = true };
        }
    }
}
