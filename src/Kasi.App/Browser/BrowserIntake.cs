using Kasi.Ipc.Messages;

namespace Kasi.App.Browser;

/// <summary>
/// One download the browser handed over. A class with no printing of its members: it holds cookie values and a URL, which are secrets
/// (CLAUDE.md rule 3), so an accidental <c>ToString</c> into a log says only the type.
/// </summary>
internal sealed class BrowserDownload(
    string url,
    string finalUrl,
    string? referrer,
    string filename,
    string mime,
    string userAgent,
    IReadOnlyList<IpcCookie> cookies)
{
    public string Url { get; } = url;

    public string FinalUrl { get; } = finalUrl;

    public string? Referrer { get; } = referrer;

    public string Filename { get; } = filename;

    public string Mime { get; } = mime;

    public string UserAgent { get; } = userAgent;

    public IReadOnlyList<IpcCookie> Cookies { get; } = cookies;

    public override string ToString() => nameof(BrowserDownload);
}

/// <summary>The links of a page chosen in the browser, waiting for the link picker (KASI-BRW-005). Same care as <see cref="BrowserDownload"/>.</summary>
internal sealed class BrowserBatch(string referrer, IReadOnlyList<IpcLink> links, IReadOnlyList<IpcCookie> cookies, string userAgent)
{
    public string Referrer { get; } = referrer;

    public IReadOnlyList<IpcLink> Links { get; } = links;

    public IReadOnlyList<IpcCookie> Cookies { get; } = cookies;

    public string UserAgent { get; } = userAgent;

    public override string ToString() => nameof(BrowserBatch);
}

/// <summary>What the UI implements to show a browser request (the Add dialog and the link picker arrive with PR 2.8d).</summary>
internal interface IBrowserRequestSink
{
    void ShowAdd(BrowserDownload download);

    void ShowPicker(BrowserBatch batch);
}

/// <summary>Where the pipe handler puts what the browser sent. It answers whether the request was durably accepted, which is what the ack means.</summary>
internal interface IBrowserIntake
{
    bool TryAdd(BrowserDownload download);

    bool TryAddBatch(BrowserBatch batch);
}

/// <summary>
/// The hand from the pipe (which runs before the UI exists) to the UI. A request is accepted when it is queued here, whether or not a
/// window is open yet: that is the "add dialog queued" case of TRD 9.2, so the ack never waits for a person. The queues are bounded so a
/// misbehaving browser cannot make Kasi hold unlimited links and cookies; a full queue refuses, and the extension then lets the browser
/// download the file.
/// </summary>
internal sealed class BrowserIntake : IBrowserIntake
{
    public const int MaxPendingDownloads = 100;
    public const int MaxPendingBatches = 8;

    private readonly Lock _gate = new();
    private readonly Queue<BrowserDownload> _downloads = new();
    private readonly Queue<BrowserBatch> _batches = new();
    private Action<Action>? _post;
    private IBrowserRequestSink? _sink;

    public int PendingDownloads
    {
        get
        {
            lock (_gate)
            {
                return _downloads.Count;
            }
        }
    }

    public int PendingBatches
    {
        get
        {
            lock (_gate)
            {
                return _batches.Count;
            }
        }
    }

    public bool TryAdd(BrowserDownload download)
    {
        ArgumentNullException.ThrowIfNull(download);
        lock (_gate)
        {
            if (_sink is null)
            {
                return Enqueue(_downloads, download, MaxPendingDownloads);
            }
        }

        Deliver(sink => sink.ShowAdd(download));
        return true;
    }

    public bool TryAddBatch(BrowserBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        lock (_gate)
        {
            if (_sink is null)
            {
                return Enqueue(_batches, batch, MaxPendingBatches);
            }
        }

        Deliver(sink => sink.ShowPicker(batch));
        return true;
    }

    /// <summary>The UI is up: hand over what waited, then deliver new requests directly. <paramref name="post"/> moves a call to the UI thread.</summary>
    public void Attach(IBrowserRequestSink sink, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(post);
        BrowserDownload[] downloads;
        BrowserBatch[] batches;
        lock (_gate)
        {
            _sink = sink;
            _post = post;
            downloads = [.. _downloads];
            batches = [.. _batches];
            _downloads.Clear();
            _batches.Clear();
        }

        foreach (var download in downloads)
        {
            Deliver(s => s.ShowAdd(download));
        }

        foreach (var batch in batches)
        {
            Deliver(s => s.ShowPicker(batch));
        }
    }

    private static bool Enqueue<T>(Queue<T> queue, T item, int limit)
    {
        if (queue.Count >= limit)
        {
            return false;
        }

        queue.Enqueue(item);
        return true;
    }

    private void Deliver(Action<IBrowserRequestSink> action)
    {
        IBrowserRequestSink sink;
        Action<Action> post;
        lock (_gate)
        {
            sink = _sink!;
            post = _post!;
        }

        post(() => action(sink));
    }
}
