namespace Kasi.App.Startup;

/// <summary>
/// Where "add these URLs" goes: a second launch with URLs, and later the browser pipe. PR 2.8d replaces the registered implementation with
/// the Add dialog; until then the main window's view model keeps the URLs it was asked to add.
/// </summary>
internal interface IAddDownloadService
{
    /// <summary>Called on the UI thread with URLs that already passed <see cref="AppArguments"/> validation.</summary>
    void RequestAdd(IReadOnlyList<string> urls);
}
