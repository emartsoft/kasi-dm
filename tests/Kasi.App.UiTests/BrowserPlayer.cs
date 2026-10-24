using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.App.UiTests;

/// <summary>
/// Plays the extension on the app's real browser pipe: it says hello, then sends what a browser would. The UI tests that start from a
/// browser (the link picker) use it, so no browser is needed and the app's real pipe server and handler are what answer.
/// </summary>
internal sealed class BrowserPlayer : IAsyncDisposable
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(30);
    private readonly KasiPipeClient _client;

    private BrowserPlayer(KasiPipeClient client) => _client = client;

    public static async Task<BrowserPlayer> ConnectAsync(AppProcess app, CancellationToken cancellationToken)
    {
        var client = await KasiPipeClient.ConnectAsync(app.BrowserPipe, _wait, cancellationToken);
        await client.SendAsync(new Hello { Id = "h0", Client = BrowserClient.Chrome, ExtensionVersion = "0.2.0", ProtocolMin = 1, ProtocolMax = 1 }, cancellationToken);
        await client.ReceiveAsync(cancellationToken).WaitAsync(_wait, cancellationToken); // Kasi answers with its rules
        return new BrowserPlayer(client);
    }

    /// <summary>Sends <c>addBatch</c> with these links and returns Kasi's answer.</summary>
    public async Task<IpcMessage?> AddBatchAsync(IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        await _client.SendAsync(
            new AddBatch
            {
                Id = "b1",
                Referrer = "https://example.com/",
                Links = [.. urls.Select(u => new IpcLink { Url = u })],
                Cookies = [],
                UserAgent = "KasiUiTest/1.0",
            },
            cancellationToken);
        return await _client.ReceiveAsync(cancellationToken).WaitAsync(_wait, cancellationToken);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
