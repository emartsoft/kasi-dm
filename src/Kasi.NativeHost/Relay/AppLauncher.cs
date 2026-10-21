using System.Diagnostics;
using Kasi.Ipc.Messages;
using Kasi.Ipc.Transport;

namespace Kasi.NativeHost.Relay;

/// <summary>
/// Connects to Kasi's pipe, starting <c>Kasi.exe --tray</c> first when nobody serves it (TC-BRW-002). The executable is the file next to the
/// host, never one found on PATH. The pipe is retried with a backoff of 50 ms doubling to 500 ms until <see cref="Window"/> has passed.
/// </summary>
internal sealed class AppLauncher(
    string appPath,
    string? pipeName = null,
    TimeProvider? time = null,
    Action<ProcessStartInfo>? start = null) : IHostPipeConnector
{
    /// <summary>The Kasi executable in the host's own folder.</summary>
    public static string NextToHost => Path.Combine(AppContext.BaseDirectory, "Kasi.exe");

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan _firstDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan _maxDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan _attempt = TimeSpan.FromMilliseconds(10);

    private readonly string _pipeName = pipeName ?? KasiPipeNames.ForCurrentUser();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Action<ProcessStartInfo> _start = start ?? (info => Process.Start(info)?.Dispose());

    public async Task<IHostPipe> ConnectAsync(CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        if (await TryConnectAsync(cancellationToken).ConfigureAwait(false) is { } running)
        {
            return running;
        }

        if (!File.Exists(appPath))
        {
            throw new AppUnavailableException(IpcErrorCode.AppNotInstalled);
        }

        // ShellExecute does not hand the browser's stdin and stdout handles to Kasi, which would keep them open and corrupt the protocol.
        _start(new ProcessStartInfo(appPath, "--tray") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(appPath)! });

        var delay = _firstDelay;
        while (true)
        {
            var remaining = Window - _time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                throw new AppUnavailableException(IpcErrorCode.AppNotRunning);
            }

            await Task.Delay(delay < remaining ? delay : remaining, _time, cancellationToken).ConfigureAwait(false);
            if (await TryConnectAsync(cancellationToken).ConfigureAwait(false) is { } connected)
            {
                return connected;
            }

            delay = delay * 2 < _maxDelay ? delay * 2 : _maxDelay;
        }
    }

    private async Task<IHostPipe?> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new KasiPipeHostPipe(await KasiPipeClient.ConnectAsync(_pipeName, _attempt, cancellationToken).ConfigureAwait(false));
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (IpcServerNotTrustedException)
        {
            // someone else holds the name: never retry into it, and never start a second Kasi because of it
            throw new AppUnavailableException(IpcErrorCode.AppNotRunning);
        }
    }
}
