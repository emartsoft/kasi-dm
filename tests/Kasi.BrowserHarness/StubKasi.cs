using System.Collections.Concurrent;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.BrowserHarness;

/// <summary>What the stub received for one <c>addDownload</c>. Cookie values are the tests' canaries, never real secrets.</summary>
internal sealed record ReceivedDownload(string Url, string FinalUrl, string? Referrer, string Filename, string Mime, IReadOnlyList<ReceivedCookie> Cookies);

internal sealed record ReceivedCookie(string Name, string Value, string Domain);

/// <summary>
/// Kasi as the browser protocol sees it: the real <see cref="KasiPipeServer"/> on the real per-user pipe (so the framing, the access list
/// and the native host are all genuine), with a handler that answers <c>hello</c> with rules and <c>addDownload</c> as the mode says.
/// </summary>
internal sealed class StubKasi(HarnessOptions options) : IAsyncDisposable
{
    private readonly ConcurrentQueue<ReceivedDownload> _received = new();
    private volatile string _mode = options.Mode;
    private volatile RulesOptions _rules = options.Rules;
    private int _answerDelayMs = options.AnswerDelayMs;
    private KasiPipeServer? _server;

    public IReadOnlyList<ReceivedDownload> Received => [.. _received];

    public bool Running => _server is not null;

    public void Clear() => _received.Clear();

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

    public ValueTask DisposeAsync() => _server?.DisposeAsync() ?? ValueTask.CompletedTask;

    private async ValueTask<IpcMessage?> HandleAsync(IpcConnection connection, IpcMessage message, CancellationToken cancellationToken)
    {
        switch (message)
        {
            case Hello:
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

            default:
                return new Ack { Id = message.Id, Ok = true };
        }
    }
}
