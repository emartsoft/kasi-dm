using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Kasi.Bench.Macro;

/// <summary>
/// <c>Kasi.TestServer</c> in a process of its own. Byte generation then does not compete with the engine for the same cores,
/// and the engine's memory and CPU can be measured without the server in them.
/// </summary>
public sealed class ServerProcess : IAsyncDisposable
{
    private readonly Process _process;

    private ServerProcess(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<ServerProcess> StartAsync(CancellationToken cancellationToken)
    {
        var port = FreePort();
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(FindServerDll());
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var process = Process.Start(start) ?? throw new InvalidOperationException("The test server did not start.");
        var address = new Uri($"http://127.0.0.1:{port}/");
        using var probe = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(2) };
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The test server exited with code {process.ExitCode}.");
            }

            try
            {
                using var head = new HttpRequestMessage(HttpMethod.Head, "files/ready/1");
                using var response = await probe.SendAsync(head, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return new ServerProcess(process, address);
                }
            }
            catch (HttpRequestException)
            {
                // not listening yet
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // the probe timed out; try again
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        process.Kill(entireProcessTree: true);
        throw new TimeoutException("The test server did not answer within 30 s.");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string FindServerDll()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "Kasi.TestServer.dll");
        if (File.Exists(Path.ChangeExtension(beside, ".runtimeconfig.json")))
        {
            return beside;
        }

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Directory
            .GetFiles(Path.Combine(root, "tests", "Kasi.TestServer", "bin"), "Kasi.TestServer.dll", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new FileNotFoundException("Build tests/Kasi.TestServer first (dotnet build).");
    }
}
