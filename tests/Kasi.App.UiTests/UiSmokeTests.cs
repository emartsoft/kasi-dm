using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Kasi.TestServer;

namespace Kasi.App.UiTests;

/// <summary>
/// TC-UI-001, KASI-UI-001: the main flows through the real Kasi.App window. Each test starts its own app with its own data folder and
/// endpoint names, and downloads from an in-process Kasi.TestServer on loopback; nothing reaches the public internet.
/// </summary>
[Trait("TC", "TC-UI-001")]
[Trait("Requirement", "KASI-UI-001")]
public sealed class UiSmokeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [UiFact]
    public async Task The_main_window_opens_with_its_toolbar_sidebar_list_and_status_bar()
    {
        await using var app = AppProcess.Start();

        var window = app.WaitForWindow(AppProcess.MainWindowName);

        foreach (var button in new[] { "Add URL", "Resume", "Pause", "Delete", "Start queue", "Scheduler", "Settings" })
        {
            window.Named(button, "a toolbar button");
        }

        window.Named("All downloads", "the sidebar entry");
        window.Named("Downloads", "the download list");
        window.Named("0 active, 0 queued", "the status bar");
    }

    [UiFact]
    public async Task A_download_added_in_the_dialog_completes_with_the_right_content_and_shows_in_the_list()
    {
        await using var server = await TestServerHost.StartAsync(cancellationToken: Ct);
        await using var app = AppProcess.Start();
        const string Seed = "ui-add";
        const long Size = 4 * 1024 * 1024;
        var window = app.WaitForWindow(AppProcess.MainWindowName);

        window.Press("Add URL");
        var dialog = app.WaitForWindow("Add download");
        dialog.SetText("Link", new Uri(server.BaseAddress, $"/files/{Seed}/{Size}").ToString());
        dialog.SetText("Save as", "added.bin");
        dialog.SetText("Folder (empty: the category folder)", app.WorkFolder);
        dialog.Press("Add");

        var path = Path.Combine(app.WorkFolder, "added.bin");
        UiHelpers.WaitFor(() => File.Exists(path) && new FileInfo(path).Length == Size ? path : null, "the finished file", window);
        (await UiHelpers.Sha256Async(path, Ct)).Should().Be(GeneratedContent.Sha256Hex(Seed, Size));
        UiHelpers.WaitFor(() => window.RowTexts().FirstOrDefault(r => r.Contains("added.bin", StringComparison.Ordinal) && r.Contains("Completed", StringComparison.Ordinal)), "the finished row in the list", window);
    }

    [UiInputFact]
    public async Task Pause_stops_a_running_download_and_Resume_starts_it_again()
    {
        await using var server = await TestServerHost.StartAsync(cancellationToken: Ct);
        await using var app = AppProcess.Start();
        var window = app.WaitForWindow(AppProcess.MainWindowName);

        window.Press("Add URL");
        var dialog = app.WaitForWindow("Add download");
        dialog.SetText("Link", new Uri(server.BaseAddress, "/files/ui-pause/6442450944").ToString()); // 6 GiB: still running after the clicks, and small enough for a CI disk to reserve
        dialog.SetText("Save as", "paused.bin");
        dialog.SetText("Folder (empty: the category folder)", app.WorkFolder);
        dialog.Press("Add");

        var row = UiHelpers.WaitFor(() => window.FindFirstDescendant(c => c.ByName("paused.bin"))?.Ancestor(ControlType.DataItem), "the new row in the list", window);
        window.SetForeground();
        row.Click(); // the grid's rows expose no selection pattern, so this is a real click on the row
        UiHelpers.WaitFor(() => window.Named(ControlType.Button, "Pause", "the Pause button") is { IsEnabled: true } b ? b : null, "Pause to be enabled by the selected row", window);
        window.Press("Pause");
        UiHelpers.WaitFor(() => window.RowTexts().FirstOrDefault(r => r.Contains("Paused", StringComparison.Ordinal)), "the row to read Paused", window);

        window.Press("Resume");
        UiHelpers.WaitFor(() => window.RowTexts().FirstOrDefault(r => r.Contains("paused.bin", StringComparison.Ordinal) && !r.Contains("Paused", StringComparison.Ordinal)), "the row to leave Paused", window);
    }
}
