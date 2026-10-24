using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Kasi.Ipc;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kasi.App.Browser;

/// <summary>Opens Kasi's Settings window on a section; false means it could not be queued.</summary>
internal interface ISettingsLauncher
{
    void Show(SettingsSection section);
}

/// <summary>
/// Kasi's side of the browser protocol (TRD 9.4, KASI-BRW-002, 004 to 006). The only place that knows the messages: it turns them into calls
/// on small interfaces, so it runs without Avalonia and without an engine.
/// <list type="bullet">
/// <item><c>hello</c> is answered with <c>rules</c> built from the settings; <c>minSizeBytes</c> goes only to an extension that said it is
/// new enough to read it (a strict parser refuses the whole message otherwise).</item>
/// <item><c>addDownload</c> and <c>addBatch</c> are acknowledged when the request is durably accepted, which is when it is queued for the
/// Add dialog or the picker, never when a person answers (ADR 0019). A request id is idempotent: the same id again gets the same ack and
/// nothing is queued twice. A refused request (a full queue) gets an <c>error</c>, and the extension lets the browser download.</item>
/// <item>Nothing here logs a URL, a file name or a cookie: counts and the message type only.</item>
/// </list>
/// </summary>
internal sealed partial class BrowserMessageHandler(
    IBrowserSettings settings,
    IBrowserIntake intake,
    ISettingsLauncher settingsLauncher,
    TimeProvider? time = null,
    ILogger? logger = null,
    IRefreshBroker? refreshes = null,
    BrowserMediaServices? media = null)
{
    public const int RememberedRequests = 1024;
    public static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(10);

    private static readonly Version _minSizeVersion = Version.Parse(IpcProtocol.MinSizeExtensionVersion);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly ConditionalWeakTable<IpcConnection, ExtensionInfo> _extensions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _accepted = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MediaCaptureResult> _captureResults = new(StringComparer.Ordinal);
    private readonly BrowserMediaServices? _media = media;

    public IpcMessageHandler Handler => HandleAsync;

    /// <summary>How many request ids are remembered for the idempotency window (bounded by <see cref="RememberedRequests"/>).</summary>
    internal int RememberedCount => _accepted.Count;

    /// <summary>The <c>rules</c> message for one connected extension, as <see cref="HandleAsync"/> sends on <c>hello</c> (also used to push a change).</summary>
    public Rules RulesFor(IpcConnection connection, string id)
    {
        var current = settings.Current;
        var sendsMinSize = _extensions.TryGetValue(connection, out var info) && info.AcceptsMinSize;
        return new Rules
        {
            Id = id,
            Enabled = current.Enabled,
            Extensions = current.Extensions,
            MimeTypes = current.MimeTypes,
            ExcludedSites = current.ExcludedSites,
            BypassKey = current.BypassKey,
            AckTimeoutMs = current.AckTimeoutMs,
            MinSizeBytes = sendsMinSize ? current.MinSizeBytes : null,
        };
    }

    /// <summary>Sends the current rules to every registered host; one that cannot be written to is dropped by the server and the rest still get them.</summary>
    public async Task PushRulesAsync(KasiPipeServer server, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(server);
        foreach (var connection in server.Connections)
        {
            try
            {
                await server.SendAsync(connection, RulesFor(connection, Guid.NewGuid().ToString("N")), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                connection.Dispose();
            }
        }
    }

    private async ValueTask<IpcMessage?> HandleAsync(IpcConnection connection, IpcMessage message, CancellationToken cancellationToken)
    {
        IpcMessage? reply;
        if (message is CaptureMedia capture)
        {
            reply = await OnCaptureAsync(capture, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            reply = message switch
            {
                Hello hello => OnHello(connection, hello),
                AddDownload add => OnceFor(add.Id, () => intake.TryAdd(ToDownload(add))),
                AddBatch batch => OnceFor(batch.Id, () => intake.TryAddBatch(new BrowserBatch(batch.Referrer, batch.Links, batch.Cookies, batch.UserAgent))),
                OpenSettings open => OnOpenSettings(open),
                LinkRefreshed refreshed => refreshes is not null && refreshes.TryComplete(refreshed) ? new Ack { Id = refreshed.Id, Ok = true } : Error(refreshed.Id, IpcErrorCode.InvalidMessage), // nobody asked: refused
                MediaDetected detected => OnMediaDetected(detected),
                Ack or IpcError => null, // not a reply to anything we are waiting for
                _ => Error(message.Id, IpcErrorCode.InvalidMessage), // Rules and RefreshLink only ever travel from Kasi to the browser
            };
        }

        LogHandled(message.GetType().Name, reply?.GetType().Name ?? "none");
        return reply;
    }

    private Ack OnMediaDetected(MediaDetected detected)
    {
        _media?.Catalog.Add(detected);
        return new Ack { Id = detected.Id, Ok = true };
    }

    private async ValueTask<IpcMessage?> OnCaptureAsync(CaptureMedia capture, CancellationToken cancellationToken)
    {
        if (_media?.Capture is not { } captureService)
        {
            return Error(capture.Id, IpcErrorCode.Internal);
        }

        if (_accepted.TryGetValue(capture.Id, out var seen) && _time.GetUtcNow() - seen < RememberFor)
        {
            _captureResults.TryGetValue(capture.Id, out var previous);
            return new Ack { Id = capture.Id, Ok = true, Detail = previous?.Status == MediaCaptureStatus.Protected ? previous.Detail : null };
        }

        var result = await captureService.CaptureAsync(capture, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case MediaCaptureStatus.Accepted:
                Remember(capture.Id, _time.GetUtcNow());
                _captureResults[capture.Id] = result;
                return new Ack { Id = capture.Id, Ok = true };
            case MediaCaptureStatus.Protected:
                Remember(capture.Id, _time.GetUtcNow());
                _captureResults[capture.Id] = result;
                return new Ack { Id = capture.Id, Ok = true, Detail = result.Detail ?? "This video is protected and can't be saved" };
            default:
                return Error(capture.Id, IpcErrorCode.Internal);
        }
    }

    private Rules OnHello(IpcConnection connection, Hello hello)
    {
        _extensions.AddOrUpdate(connection, new ExtensionInfo(Version.TryParse(hello.ExtensionVersion, out var version) && version >= _minSizeVersion));
        return RulesFor(connection, hello.Id);
    }

    private Ack OnOpenSettings(OpenSettings open)
    {
        settingsLauncher.Show(open.Section ?? SettingsSection.Browser);
        return new Ack { Id = open.Id, Ok = true };
    }

    private IpcMessage OnceFor(string id, Func<bool> accept)
    {
        var now = _time.GetUtcNow();
        if (_accepted.TryGetValue(id, out var seen) && now - seen < RememberFor)
        {
            return new Ack { Id = id, Ok = true };
        }

        if (!accept())
        {
            return Error(id, IpcErrorCode.Internal);
        }

        Remember(id, now);
        return new Ack { Id = id, Ok = true };
    }

    private void Remember(string id, DateTimeOffset now)
    {
        _accepted[id] = now;
        if (_accepted.Count <= RememberedRequests)
        {
            return;
        }

        // expired ids first, then the oldest, so the memory stays bounded whatever a peer sends
        foreach (var (key, _) in _accepted.Where(entry => now - entry.Value >= RememberFor))
        {
            _accepted.TryRemove(key, out _);
        }

        foreach (var (key, _) in _accepted.OrderBy(entry => entry.Value).Take(Math.Max(0, _accepted.Count - RememberedRequests)))
        {
            _accepted.TryRemove(key, out _);
        }
    }

    private static BrowserDownload ToDownload(AddDownload add) =>
        new(add.Url, add.FinalUrl, add.Referrer, add.Filename, add.Mime, add.UserAgent, add.Cookies);

    private static IpcError Error(string id, IpcErrorCode code) => new() { Id = id, Code = code };

    [LoggerMessage(Level = LogLevel.Debug, Message = "Browser message {Type} answered with {Reply}.")]
    private partial void LogHandled(string type, string reply);

    private sealed record ExtensionInfo(bool AcceptsMinSize);
}
