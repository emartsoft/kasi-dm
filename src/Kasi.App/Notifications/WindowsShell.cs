using System.ComponentModel;
using System.Diagnostics;

namespace Kasi.App.Notifications;

/// <summary>The shell of the machine: the default program for a file, and Explorer for a folder.</summary>
internal sealed class WindowsShell : IShell
{
    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = false });
        }
    }

    public void ShowInFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        if (File.Exists(path))
        {
            start.ArgumentList.Add("/select,");
            start.ArgumentList.Add(path);
        }
        else
        {
            start.ArgumentList.Add(folder);
        }

        Start(start);
    }

    private static void Start(ProcessStartInfo start)
    {
        try
        {
            using var process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // no program for this type, or Explorer is not available: nothing to do for the user here
        }
    }
}
