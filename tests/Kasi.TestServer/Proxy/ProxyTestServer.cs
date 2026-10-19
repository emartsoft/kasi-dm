using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kasi.TestServer.Proxy;

/// <summary>Loopback HTTP/CONNECT or SOCKS5 proxy for KASI-ENG-011 integration tests.</summary>
public sealed class ProxyTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly Task _acceptLoop;
    private readonly int _originPort;
    private readonly string? _user;
    private readonly string? _password;
    private readonly TestServerCertificates? _certificates;
    private int _nextClient;
    private int _connections;
    private int _connects;
    private int _socksHandshakes;
    private int _authFailures;
    private long _bytesRelayed;

    private ProxyTestServer(TcpListener listener, int originPort, bool socks5, string? user, string? password,
        TestServerCertificates? certificates)
    {
        _listener = listener;
        _originPort = originPort;
        IsSocks5 = socks5;
        _user = user;
        _password = password;
        _certificates = certificates;
        _acceptLoop = AcceptAsync();
    }

    public bool IsSocks5 { get; }

    public Uri Address => new($"{(IsSocks5 ? "socks5" : _certificates is null ? "http" : "https")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public X509Certificate2? RootCertificate => _certificates?.RootCertificate;

    public int Connections => Volatile.Read(ref _connections);

    public int Connects => Volatile.Read(ref _connects);

    public int SocksHandshakes => Volatile.Read(ref _socksHandshakes);

    public int AuthFailures => Volatile.Read(ref _authFailures);

    public long BytesRelayed => Interlocked.Read(ref _bytesRelayed);

    public static ProxyTestServer Start(int originPort, bool socks5 = false, string? user = null, string? password = null,
        bool tlsProxy = false)
    {
        if (socks5 && tlsProxy)
        {
            throw new ArgumentException("The test SOCKS5 proxy does not use TLS.");
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new ProxyTestServer(listener, originPort, socks5, user, password,
            tlsProxy ? TestServerCertificates.Create(TestServerTlsMode.Valid) : null);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
#pragma warning disable VSTHRD003 // the accept loop is started by this instance in its constructor
        await _acceptLoop;
#pragma warning restore VSTHRD003
        await Task.WhenAll(_clients.Values);
        _certificates?.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (_stop.IsCancellationRequested)
            {
                break;
            }

            Interlocked.Increment(ref _connections);
            var id = Interlocked.Increment(ref _nextClient);
            var task = ServeClientAsync(client);
            _clients[id] = task;
            _ = task.ContinueWith(completed => _clients.TryRemove(id, out _), TaskScheduler.Default);
        }
    }

    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var network = client.GetStream();
                using Stream stream = _certificates is { ServerCertificate: { } certificate }
                    ? await AuthenticateProxyTlsAsync(network, certificate, _stop.Token)
                    : network;
                if (IsSocks5)
                {
                    await ServeSocksAsync(stream, _stop.Token);
                }
                else
                {
                    await ServeHttpAsync(stream, _stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or OperationCanceledException)
            {
                // A test client may close a tunnel while a segment is being cancelled.
            }
        }
    }

    private static async Task<SslStream> AuthenticateProxyTlsAsync(Stream network, X509Certificate2 certificate,
        CancellationToken ct)
    {
        var tls = new SslStream(network, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false,
                checkCertificateRevocation: false).WaitAsync(ct);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync();
            throw;
        }
    }

    private async Task ServeHttpAsync(Stream client, CancellationToken ct)
    {
        var requestLine = await ReadLineAsync(client, ct);
        if (requestLine is null)
        {
            return;
        }

        var headers = new List<string>();
        string? line;
        while (!string.IsNullOrEmpty(line = await ReadLineAsync(client, ct)))
        {
            headers.Add(line);
        }

        if (_user is not null && !HasBasicCredential(headers, _user, _password ?? string.Empty))
        {
            Interlocked.Increment(ref _authFailures);
            await WriteAsciiAsync(client,
                "HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"Kasi Test Proxy\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", ct);
            return;
        }

        var parts = requestLine.Split(' ', 3);
        if (parts.Length != 3)
        {
            return;
        }

        using var origin = new TcpClient();
        await origin.ConnectAsync(IPAddress.Loopback, _originPort, ct);
        using var upstream = origin.GetStream();
        if (parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _connects);
            await WriteAsciiAsync(client, "HTTP/1.1 200 Connection Established\r\n\r\n", ct);
            await RelayBothAsync(client, upstream, ct);
            return;
        }

        if (!Uri.TryCreate(parts[1], UriKind.Absolute, out var uri))
        {
            return;
        }

        await WriteAsciiAsync(upstream, $"{parts[0]} {uri.PathAndQuery} {parts[2]}\r\n", ct);
        foreach (var header in headers)
        {
            if (header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase)
                || header.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await WriteAsciiAsync(upstream, header + "\r\n", ct);
        }

        await WriteAsciiAsync(upstream, "Connection: close\r\n\r\n", ct);
        await RelayAsync(upstream, client, ct);
    }

    private async Task ServeSocksAsync(Stream client, CancellationToken ct)
    {
        var hello = new byte[2];
        await client.ReadExactlyAsync(hello, ct);
        if (hello[0] != 5)
        {
            return;
        }

        var methods = new byte[hello[1]];
        await client.ReadExactlyAsync(methods, ct);
        var neededMethod = _user is null ? (byte)0 : (byte)2;
        var method = methods.Contains(neededMethod) ? neededMethod : (byte)255;
        await client.WriteAsync(new byte[] { 5, method }, ct);
        if (method == 255)
        {
            Interlocked.Increment(ref _authFailures);
            return;
        }

        if (method == 2)
        {
            var authHeader = new byte[2];
            await client.ReadExactlyAsync(authHeader, ct);
            var user = new byte[authHeader[1]];
            await client.ReadExactlyAsync(user, ct);
            var length = new byte[1];
            await client.ReadExactlyAsync(length, ct);
            var password = new byte[length[0]];
            await client.ReadExactlyAsync(password, ct);
            var valid = authHeader[0] == 1 && Encoding.UTF8.GetString(user) == _user
                && Encoding.UTF8.GetString(password) == _password;
            await client.WriteAsync(new byte[] { 1, valid ? (byte)0 : (byte)1 }, ct);
            if (!valid)
            {
                Interlocked.Increment(ref _authFailures);
                return;
            }
        }

        var request = new byte[4];
        await client.ReadExactlyAsync(request, ct);
        if (request[0] != 5 || request[1] != 1)
        {
            return;
        }

        var addressLength = request[3] switch
        {
            1 => 4,
            4 => 16,
            3 => await ReadByteAsync(client, ct),
            _ => 0,
        };
        if (addressLength == 0)
        {
            return;
        }

        var destination = new byte[addressLength + 2];
        await client.ReadExactlyAsync(destination, ct);
        using var origin = new TcpClient();
        await origin.ConnectAsync(IPAddress.Loopback, _originPort, ct);
        Interlocked.Increment(ref _socksHandshakes);
        await client.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 }, ct);
        using var upstream = origin.GetStream();
        await RelayBothAsync(client, upstream, ct);
    }

    private async Task RelayBothAsync(Stream first, Stream second, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forward = RelayAsync(first, second, linked.Token);
        var backward = RelayAsync(second, first, linked.Token);
        await Task.WhenAny(forward, backward);
        await linked.CancelAsync();
        try
        {
            await Task.WhenAll(forward, backward);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RelayAsync(Stream source, Stream destination, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0)
            {
                return;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            Interlocked.Add(ref _bytesRelayed, read);
        }
    }

    private static bool HasBasicCredential(IEnumerable<string> headers, string user, string password)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password));
        return headers.Any(header => header.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase)
            && string.Equals(header[(header.IndexOf(':') + 1)..].Trim(), expected, StringComparison.Ordinal));
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(128);
        while (buffer.Count < 16 * 1024)
        {
            var value = await ReadByteAsync(stream, ct);
            if (value < 0)
            {
                return null;
            }

            if (value == '\n')
            {
                if (buffer.Count > 0 && buffer[^1] == '\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return Encoding.ASCII.GetString([.. buffer]);
            }

            buffer.Add((byte)value);
        }

        throw new IOException("Proxy request header is too long.");
    }

    private static async Task<int> ReadByteAsync(Stream stream, CancellationToken ct)
    {
        var one = new byte[1];
        return await stream.ReadAsync(one, ct) == 0 ? -1 : one[0];
    }

    private static Task WriteAsciiAsync(Stream stream, string value, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(value), ct).AsTask();
}
