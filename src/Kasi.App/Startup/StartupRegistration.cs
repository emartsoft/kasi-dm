using Microsoft.Win32;

namespace Kasi.App.Startup;

/// <summary>The user's “Start with Windows” choice (KASI-SYS-003).</summary>
internal interface IStartupRegistration
{
    bool IsEnabled();

    /// <summary>Adds or removes exactly one HKCU Run value. A portable build calls this only after the user opts in.</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Writes <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>. The value points at the installed executable and passes
/// <c>--tray</c>, so boot starts hidden. A registry root and key path are injectable for the private-hive test.
/// </summary>
internal sealed class WindowsStartupRegistration : IStartupRegistration
{
    internal const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "Kasi";

    private readonly RegistryKey _userRoot;
    private readonly string _runKeyPath;
    private readonly string _executable;

    public WindowsStartupRegistration(string? executable = null, RegistryKey? userRoot = null, string? runKeyPath = null)
    {
        _executable = executable ?? Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Kasi.exe");
        _userRoot = userRoot ?? Registry.CurrentUser;
        _runKeyPath = runKeyPath ?? DefaultRunKey;
    }

    public bool IsEnabled()
    {
        using var run = _userRoot.OpenSubKey(_runKeyPath);
        return run?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public void SetEnabled(bool enabled)
    {
        using var run = _userRoot.CreateSubKey(_runKeyPath, writable: true)
            ?? throw new InvalidOperationException("The Windows Run key could not be opened.");
        if (enabled)
        {
            run.SetValue(ValueName, $"\"{_executable}\" --tray", RegistryValueKind.String);
        }
        else
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
