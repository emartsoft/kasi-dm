using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Connections;

namespace Kasi.TestServer.Chaos;

internal sealed record ConnectionInfo(int Index, int ClientOrdinal);

/// <summary>
/// Numbers connections in accept order (for per-connection throttling) and counts open connections per client address
/// (for the connection cap). Hooked into Kestrel as connection middleware.
/// </summary>
internal sealed class ConnectionTracker(TestServerMetrics metrics)
{
    private readonly ConcurrentDictionary<string, ConnectionInfo> _connections = new();
    private readonly Dictionary<string, int> _openPerClient = [];
    private readonly Lock _gate = new();
    private int _nextIndex;

    public ConnectionInfo? Get(string connectionId) => _connections.GetValueOrDefault(connectionId);

    public async Task TrackAsync(ConnectionContext context, Func<Task> next)
    {
        var client = (context.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
        ConnectionInfo info;
        lock (_gate)
        {
            var open = _openPerClient.GetValueOrDefault(client) + 1;
            _openPerClient[client] = open;
            info = new ConnectionInfo(_nextIndex++, open);
        }

        _connections[context.ConnectionId] = info;
        metrics.ConnectionAccepted();
        try
        {
            await next();
        }
        finally
        {
            _connections.TryRemove(context.ConnectionId, out _);
            metrics.ConnectionClosed(context.ConnectionId);
            lock (_gate)
            {
                _openPerClient[client]--;
            }
        }
    }
}
