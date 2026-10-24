using System.Diagnostics;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

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
    private readonly UIA3Automation _automation = new();

    private AppProcess(Process process, string dataFolder, string workFolder)
    {
        _process = process;
        DataFolder = dataFolder;
        WorkFolder = workFolder;
    }

    public string DataFolder { get; }

    /// <summary>A folder under the test's root for the files the test downloads; removed with the app.</summary>
    public string WorkFolder { get; }

    public static AppProcess Start(params string[] arguments)
    {
        var root = Path.Combine(Path.GetTempPath(), "kasi-ui-" + Guid.NewGuid().ToString("N")[..10]);
        var data = Path.Combine(root, "data");
        var work = Path.Combine(root, "files");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(work);

        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Kasi.App.exe")) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["KASI_DATA_DIR"] = data;
        start.Environment["KASI_ENDPOINT_SUFFIX"] = "ui" + Guid.NewGuid().ToString("N")[..8];
        return new AppProcess(Process.Start(start)!, data, work);
    }

    public bool HasExited => _process.HasExited;

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
        return ok ? found! : throw new TimeoutException($"No window named '{name}' appeared. Windows: {string.Join(", ", TopLevelWindows().Select(w => $"'{w.Name}'"))}");
    }

    public IReadOnlyList<Window> TopLevelWindows() =>
        _process.HasExited ? [] : Application.Attach(_process).GetAllTopLevelWindows(_automation);

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
