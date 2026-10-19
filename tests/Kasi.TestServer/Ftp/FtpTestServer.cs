using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kasi.TestServer.Ftp;

/// <summary>In-process passive FTP/explicit FTPS source for TC-ENG-022.</summary>
public sealed class FtpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private readonly ConcurrentQueue<long> _restOffsets = new();
    private readonly Task _acceptLoop;
    private readonly TestServerCertificates? _certificates;
    private readonly string _seed;
    private readonly string? _user;
    private readonly string? _password;
    private readonly bool _restSupported;
    private readonly bool _mdtmSupported;
    private int _nextClient;
    private int _connections;
    private int _authFailures;
    private int _dropsRemaining;
    private int _activeDataTransfers;
    private long _size;
    private long _bytesSent;
    private long _dropAfterBytes;
    private DateTime _modifiedUtc;

    private FtpTestServer(TcpListener listener, string seed, long size, bool ftps, string? user,
        string? password, bool restSupported, bool mdtmSupported)
    {
        _listener = listener;
        _seed = seed;
        _size = size;
        _certificates = ftps ? TestServerCertificates.Create(TestServerTlsMode.Valid) : null;
        _user = user;
        _password = password;
        _restSupported = restSupported;
        _mdtmSupported = mdtmSupported;
        _modifiedUtc = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        _acceptLoop = AcceptAsync();
    }

    public Uri BaseAddress => new($"{(_certificates is null ? "ftp" : "ftps")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

    public X509Certificate2? RootCertificate => _certificates?.RootCertificate;

    public int Connections => Volatile.Read(ref _connections);

    public int AuthFailures => Volatile.Read(ref _authFailures);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public int ActiveDataTransfers => Volatile.Read(ref _activeDataTransfers);

    public IReadOnlyList<long> RestOffsets => _restOffsets.ToArray();

    public TimeSpan DataChunkDelay { get; set; }

    public static FtpTestServer Start(string seed, long size, bool ftps = false, string? user = null,
        string? password = null, bool restSupported = true, bool mdtmSupported = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new FtpTestServer(listener, seed, size, ftps, user, password, restSupported, mdtmSupported);
    }

    public void SetSize(long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        Interlocked.Exchange(ref _size, size);
    }

    public void SetModifiedTime(DateTime modifiedUtc) => _modifiedUtc = modifiedUtc.ToUniversalTime();

    public void DropOneDataConnectionAfter(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        Interlocked.Exchange(ref _dropAfterBytes, bytes);
        Interlocked.Exchange(ref _dropsRemaining, 1);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
#pragma warning disable VSTHRD003 // the accept loop is started by this instance in its constructor
        await _acceptLoop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        _certificates?.Dispose();
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient socket;
            try
            {
                socket = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
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
            var task = ServeAsync(socket);
            _clients[id] = task;
            _ = task.ContinueWith(completed => _clients.TryRemove(id, out _), TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(TcpClient socket)
    {
        using (socket)
        using (var network = socket.GetStream())
        {
            Stream control = network;
            TcpListener? passive = null;
            long restart = 0;
            var encrypted = false;
            var dataEncrypted = false;
            var authenticated = false;
            var user = string.Empty;
            try
            {
                await WriteAsync(control, "220 Kasi FTP test server ready\r\n", _stop.Token).ConfigureAwait(false);
                while (!_stop.IsCancellationRequested)
                {
                    var line = await ReadLineAsync(control, _stop.Token).ConfigureAwait(false);
                    if (line is null)
                    {
                        return;
                    }

                    var split = line.Split(' ', 2);
                    var command = split[0].ToUpperInvariant();
                    var argument = split.Length == 2 ? split[1] : string.Empty;
                    switch (command)
                    {
                        case "AUTH" when argument.Equals("TLS", StringComparison.OrdinalIgnoreCase) && _certificates is not null:
                            await WriteAsync(control, "234 Start TLS\r\n", _stop.Token).ConfigureAwait(false);
                            var tls = new SslStream(control, leaveInnerStreamOpen: true);
                            await tls.AuthenticateAsServerAsync(_certificates.ServerCertificate!,
                                clientCertificateRequired: false, checkCertificateRevocation: false)
                                .WaitAsync(_stop.Token).ConfigureAwait(false);
                            control = tls;
                            encrypted = true;
                            break;
                        case "PBSZ":
                            await WriteAsync(control, encrypted ? "200 PBSZ accepted\r\n" : "503 TLS required\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "PROT":
                            dataEncrypted = encrypted && argument.Equals("P", StringComparison.OrdinalIgnoreCase);
                            await WriteAsync(control, dataEncrypted ? "200 Data protection enabled\r\n" : "536 Protected data required\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "USER":
                            user = argument;
                            await WriteAsync(control, "331 Password required\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "PASS":
                            authenticated = _user is null
                                ? user.Equals("anonymous", StringComparison.OrdinalIgnoreCase)
                                : user == _user && argument == _password;
                            if (!authenticated)
                            {
                                Interlocked.Increment(ref _authFailures);
                            }

                            await WriteAsync(control, authenticated ? "230 Login successful\r\n" : "530 Login incorrect\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "FEAT":
                            var features = "211-Features\r\n SIZE\r\n EPSV\r\n UTF8\r\n"
                                + (_mdtmSupported ? " MDTM\r\n" : string.Empty)
                                + (_restSupported ? " REST STREAM\r\n" : string.Empty)
                                + "211 End\r\n";
                            await WriteAsync(control, features, _stop.Token).ConfigureAwait(false);
                            break;
                        case "SYST":
                            await WriteAsync(control, "215 UNIX Type: L8\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "PWD":
                            await WriteAsync(control, "257 \"/\"\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "TYPE":
                        case "OPTS":
                        case "CLNT":
                        case "NOOP":
                        case "HOST":
                            await WriteAsync(control, "200 Command okay\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "SIZE":
                            await WriteAsync(control, authenticated && IsFile(argument)
                                ? $"213 {Interlocked.Read(ref _size).ToString(CultureInfo.InvariantCulture)}\r\n"
                                : "550 File unavailable\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "MDTM":
                            await WriteAsync(control, _mdtmSupported && authenticated && IsFile(argument)
                                ? $"213 {_modifiedUtc:yyyyMMddHHmmss}\r\n" : "500 MDTM unavailable\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "EPSV":
                        case "PASV":
                            passive?.Dispose();
                            passive = new TcpListener(IPAddress.Loopback, 0);
                            passive.Start();
                            var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                            var answer = command == "EPSV"
                                ? $"229 Entering Extended Passive Mode (|||{port}|)\r\n"
                                : $"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})\r\n";
                            await WriteAsync(control, answer, _stop.Token).ConfigureAwait(false);
                            break;
                        case "REST":
                            if (_restSupported && long.TryParse(argument, CultureInfo.InvariantCulture, out var offset))
                            {
                                restart = offset;
                                _restOffsets.Enqueue(offset);
                                await WriteAsync(control, "350 Restart position accepted\r\n", _stop.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                await WriteAsync(control, "502 REST unavailable\r\n", _stop.Token).ConfigureAwait(false);
                            }

                            break;
                        case "RETR":
                            if (!authenticated || !IsFile(argument) || passive is null)
                            {
                                await WriteAsync(control, "550 File unavailable\r\n", _stop.Token).ConfigureAwait(false);
                                break;
                            }

                            await WriteAsync(control, "150 Opening binary data connection\r\n", _stop.Token).ConfigureAwait(false);
                            var clean = await SendDataAsync(passive, restart, dataEncrypted, _stop.Token).ConfigureAwait(false);
                            passive.Dispose();
                            passive = null;
                            restart = 0;
                            await WriteAsync(control, clean ? "226 Transfer complete\r\n" : "426 Connection closed\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                        case "QUIT":
                            await WriteAsync(control, "221 Goodbye\r\n", _stop.Token).ConfigureAwait(false);
                            return;
                        default:
                            await WriteAsync(control, "502 Command not implemented\r\n", _stop.Token).ConfigureAwait(false);
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or OperationCanceledException)
            {
            }
            finally
            {
                passive?.Dispose();
                if (!ReferenceEquals(control, network))
                {
                    await control.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<bool> SendDataAsync(TcpListener listener, long offset, bool encrypt, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeDataTransfers);
        try
        {
            using var socket = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var network = socket.GetStream();
            using Stream data = encrypt && _certificates is not null
                ? await AuthenticateDataAsync(network, _certificates.ServerCertificate!, ct).ConfigureAwait(false)
                : network;
            var size = Interlocked.Read(ref _size);
            var drop = Interlocked.CompareExchange(ref _dropsRemaining, 0, 1) == 1
                ? Interlocked.Read(ref _dropAfterBytes) : long.MaxValue;
            long sent = 0;
            var buffer = new byte[32 * 1024];
            while (offset < size)
            {
                var take = (int)Math.Min(buffer.Length, Math.Min(size - offset, drop - sent));
                if (take == 0)
                {
                    return false;
                }

                GeneratedContent.Fill(_seed, offset, buffer.AsSpan(0, take));
                await data.WriteAsync(buffer.AsMemory(0, take), ct).ConfigureAwait(false);
                offset += take;
                sent += take;
                Interlocked.Add(ref _bytesSent, take);
                if (DataChunkDelay > TimeSpan.Zero)
                {
                    await Task.Delay(DataChunkDelay, ct).ConfigureAwait(false);
                }
            }

            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _activeDataTransfers);
        }
    }

    private bool IsFile(string path) => path.EndsWith("/" + _seed, StringComparison.Ordinal)
        || path.Contains("/" + _seed + "/", StringComparison.Ordinal);

    private static async Task<SslStream> AuthenticateDataAsync(Stream network, X509Certificate2 certificate,
        CancellationToken ct)
    {
        var tls = new SslStream(network, leaveInnerStreamOpen: true);
        try
        {
            await tls.AuthenticateAsServerAsync(certificate, clientCertificateRequired: false,
                checkCertificateRevocation: false).WaitAsync(ct).ConfigureAwait(false);
            return tls;
        }
        catch
        {
            await tls.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new List<byte>(128);
        while (buffer.Count < 16 * 1024)
        {
            var one = new byte[1];
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
            {
                return null;
            }

            if (one[0] == '\n')
            {
                if (buffer.Count > 0 && buffer[^1] == '\r')
                {
                    buffer.RemoveAt(buffer.Count - 1);
                }

                return Encoding.ASCII.GetString([.. buffer]);
            }

            buffer.Add(one[0]);
        }

        throw new IOException("FTP control line is too long.");
    }

    private static Task WriteAsync(Stream stream, string message, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(message), ct).AsTask();
}
