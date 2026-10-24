using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Kasi.Ipc.Transport;

namespace Kasi.App.UiTests;

/// <summary>
/// One running Kasi.App.exe with its own data folder and endpoint names, so it never meets a Kasi the developer is running and
/// two tests never share a pipe. Disposing ends the whole process tree and removes the data folder.
/// </summary>
public sealed class AppProcess : IAsyncDisposable
{
    public const string MainWindowName = "Kasi";

    private static readonly TimeSpan _windowWait = TimeSpan.FromSeconds(60);
    private readonly Process _process;
    private readonly ProcessStartInfo _start;
    private readonly UIA3Automation _automation = new();
    private readonly System.Text.StringBuilder _output = new();

    private AppProcess(Process process, ProcessStartInfo start, string dataFolder, string workFolder)
    {
        _process = process;
        _start = start;
        DataFolder = dataFolder;
        WorkFolder = workFolder;
    }

    public string DataFolder { get; }

    /// <summary>Where Kasi saves a download that names no folder (<c>KASI_DOWNLOADS_DIR</c>), so a test never writes into the user's Downloads.</summary>
    public string DownloadsFolder => Path.Combine(Path.GetDirectoryName(DataFolder)!, "downloads");

    /// <summary>The name of this instance's browser pipe: the per-user name with the test suffix this app was started with.</summary>
    public string BrowserPipe { get; private set; } = string.Empty;

    /// <summary>A folder under the test's root for the files the test downloads; removed with the app.</summary>
    public string WorkFolder { get; }

    public static AppProcess Start(params string[] arguments)
    {
        var root = Path.Combine(Path.GetTempPath(), "kasi-ui-" + Guid.NewGuid().ToString("N")[..10]);
        var data = Path.Combine(root, "data");
        var work = Path.Combine(root, "files");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(work);

        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "app", "Kasi.App.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["KASI_DATA_DIR"] = data;
        start.Environment["KASI_DOWNLOADS_DIR"] = Path.Combine(root, "downloads");
        var suffix = "ui" + Guid.NewGuid().ToString("N")[..8];
        start.Environment["KASI_ENDPOINT_SUFFIX"] = suffix;
        var app = new AppProcess(Process.Start(start)!, start, data, work);
        app.BrowserPipe = KasiPipeNames.ForCurrentUser() + "-" + suffix;
        app._process.OutputDataReceived += (_, e) => app.Capture(e.Data);
        app._process.ErrorDataReceived += (_, e) => app.Capture(e.Data);
        app._process.BeginOutputReadLine();
        app._process.BeginErrorReadLine();
        return app;
    }

    public bool HasExited => _process.HasExited;

    /// <summary>
    /// Starts Kasi.App.exe again with the same endpoint names, as a user does by opening Kasi a second time: the running instance
    /// takes over (shows its window) and the new process ends.
    /// </summary>
    public async Task LaunchAgainAsync(CancellationToken cancellationToken)
    {
        using var second = Process.Start(new ProcessStartInfo(_start.FileName)
        {
            UseShellExecute = false,
            Environment = { ["KASI_DATA_DIR"] = _start.Environment["KASI_DATA_DIR"], ["KASI_DOWNLOADS_DIR"] = _start.Environment["KASI_DOWNLOADS_DIR"], ["KASI_ENDPOINT_SUFFIX"] = _start.Environment["KASI_ENDPOINT_SUFFIX"] },
        })!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        await second.WaitForExitAsync(limit.Token).ConfigureAwait(false);
    }

    /// <summary>What the app wrote to its output and error streams so far, for a failure message.</summary>
    public string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>The tail of the app's own log files in its data folder (they are redacted by design).</summary>
    private string LogTail()
    {
        var text = new System.Text.StringBuilder();
        try
        {
            foreach (var file in Directory.EnumerateFiles(DataFolder, "*.log", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var length = (int)Math.Min(stream.Length, 4000);
                stream.Seek(-length, SeekOrigin.End);
                var buffer = new byte[length];
                stream.ReadExactly(buffer);
                text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"--- {Path.GetFileName(file)}").AppendLine(System.Text.Encoding.UTF8.GetString(buffer));
            }
        }
        catch (IOException)
        {
            // a log still being written is a diagnostic, never a reason to hide the real failure
        }

        return text.ToString();
    }

    private void Capture(string? line)
    {
        if (line is not null)
        {
            lock (_output)
            {
                _output.AppendLine(line);
            }
        }
    }

    /// <summary>The window with this name, waiting for it to appear; the wait is on the window, not on a fixed time.</summary>
    public Window WaitForWindow(string name, TimeSpan? timeout = null)
    {
        Window? found = null;
        var ok = FlaUI.Core.Tools.Retry.WhileNull(
            () =>
            {
                found = TopLevelWindows().FirstOrDefault(w => w.Name == name);
                return found;
            },
            timeout ?? _windowWait,
            TimeSpan.FromMilliseconds(200)).Success;
        return ok
            ? found!
            : throw new TimeoutException($"No window named '{name}' appeared. Windows: {string.Join(", ", TopLevelWindows().Select(w => $"'{w.Name}'"))}. {(_process.HasExited ? $"The app had exited with code {_process.ExitCode}." : "The app was still running.")} Output:{Environment.NewLine}{Output}{Environment.NewLine}{LogTail()}");
    }

    /// <summary>
    /// The app's windows. A modal dialog (Delete) is a child of the window that opened it in the UI Automation tree, not a top-level
    /// window of its own, so the owned windows of each top-level window are listed too.
    /// </summary>
    public IReadOnlyList<Window> TopLevelWindows()
    {
        if (_process.HasExited)
        {
            return [];
        }

        var top = Application.Attach(_process).GetAllTopLevelWindows(_automation);
        return [.. top, .. top.SelectMany(w => w.FindAllChildren(c => c.ByControlType(FlaUI.Core.Definitions.ControlType.Window)).Select(c => c.AsWindow()))];
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await _process.WaitForExitAsync(limit.Token).ConfigureAwait(false); // a Kasi left running would hold the next test's files
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }

        _automation.Dispose();
        _process.Dispose();
        TryDelete(Path.GetDirectoryName(DataFolder)!);
    }

    private static void TryDelete(string folder)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200); // the process has just ended and may still hold a handle
            }
        }
    }
}
