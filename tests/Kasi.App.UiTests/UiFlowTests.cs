using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Kasi.TestServer;

namespace Kasi.App.UiTests;

/// <summary>
/// M2.13b: the other windows and flows of the real Kasi.App, after the first three of <see cref="UiSmokeTests"/> (TC-UI-001, TC-UI-003,
/// KASI-UI-001, KASI-UI-003, KASI-UI-004, KASI-QUE-003, KASI-BRW-005). Same isolation: one app, data folder and endpoint names per test.
/// </summary>
[Trait("Requirement", "KASI-UI-001")]
public sealed class UiFlowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void WaitUntilGone(AppProcess app, string windowName) =>
        UiHelpers.WaitFor(() => app.TopLevelWindows().Any(w => w.Name == windowName) ? null : "gone", $"the '{windowName}' window to close");

    [UiFact]
    [Trait("Requirement", "KASI-QUE-003")]
    public async Task The_scheduler_window_opens_with_its_three_tabs_and_Cancel_closes_it()
    {
        await using var app = AppProcess.Start();
        var window = app.WaitForWindow(AppProcess.MainWindowName);

        window.Press("Scheduler");
        var scheduler = app.WaitForWindow("Scheduler");

        foreach (var tab in new[] { "Queue settings", "Schedule", "Post-completion" })
        {
            scheduler.Named(ControlType.TabItem, tab, "a tab");
        }

        scheduler.Named(ControlType.ComboBox, "Select Queue", "the queue chooser");
        scheduler.Press("Cancel");
        WaitUntilGone(app, "Scheduler");
    }

    [UiFact]
    [Trait("TC", "TC-BRW-005")]
    [Trait("Requirement", "KASI-BRW-005")]
    public async Task Links_a_browser_sends_open_the_picker_and_only_the_ticked_ones_download()
    {
        await using var server = await TestServerHost.StartAsync(cancellationToken: Ct);
        await using var app = AppProcess.Start();
        var window = app.WaitForWindow(AppProcess.MainWindowName);
        await using var browser = await BrowserPlayer.ConnectAsync(app, Ct);

        var answer = await browser.AddBatchAsync([new Uri(server.BaseAddress, "/files/pick-a/1024").ToString(), new Uri(server.BaseAddress, "/files/pick-b/2048").ToString()], Ct);
        var picker = app.WaitForWindow("Choose links to download");
        picker.SetText("Folder (empty: the category folder)", app.WorkFolder);
        picker.SetText("Filter by name or address", "1024");
        UiHelpers.WaitFor(() => picker.FindAllDescendants(c => c.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name.Contains("1 shown", StringComparison.Ordinal)), "the filter to leave one link shown", picker);
        picker.Press("Select shown");
        UiHelpers.WaitFor(() => picker.FindAllDescendants(c => c.ByControlType(ControlType.Text)).FirstOrDefault(t => t.Name.StartsWith("1 of 2 selected", StringComparison.Ordinal)), "one link selected", picker);
        picker.Press("Add selected");

        answer.Should().BeOfType<Kasi.Ipc.Messages.Ack>().Which.Ok.Should().BeTrue();
        var path = UiHelpers.WaitForFile(app, "1024", 1024, window);
        (await UiHelpers.Sha256Async(path, Ct)).Should().Be(GeneratedContent.Sha256Hex("pick-a", 1024));
        File.Exists(Path.Combine(app.WorkFolder, "2048")).Should().BeFalse("only the ticked link was added");
    }

    [UiFact]
    [Trait("TC", "TC-UI-003")]
    [Trait("Requirement", "KASI-UI-004")]
    public async Task Closing_the_window_leaves_Kasi_running_and_opening_Kasi_again_brings_the_window_back()
    {
        await using var app = AppProcess.Start();
        var window = app.WaitForWindow(AppProcess.MainWindowName);

        window.Press("Close");
        WaitUntilGone(app, AppProcess.MainWindowName);
        app.HasExited.Should().BeFalse("closing the window hides it to the tray; only Quit ends Kasi");
        await app.LaunchAgainAsync(Ct);

        app.WaitForWindow(AppProcess.MainWindowName);
        app.HasExited.Should().BeFalse();
    }

    [UiInputFact]
    [Trait("Requirement", "KASI-UI-001")]
    public async Task Delete_asks_first_and_with_the_files_option_removes_the_row_and_the_file()
    {
        await using var server = await TestServerHost.StartAsync(cancellationToken: Ct);
        await using var app = AppProcess.Start();
        var window = app.WaitForWindow(AppProcess.MainWindowName);
        window.AddDownload(app, new Uri(server.BaseAddress, "/files/ui-delete/1048576").ToString(), "doomed.bin");
        var path = UiHelpers.WaitForFile(app, "doomed.bin", 1048576, window);
        var row = UiHelpers.WaitFor(() => window.FindFirstDescendant(c => c.ByName("doomed.bin"))?.Ancestor(ControlType.DataItem), "the row in the list", window);

        window.SetForeground();
        row.Click();
        window.Press("Delete");
        var dialog = app.WaitForWindow("Delete downloads");
        dialog.Named(ControlType.CheckBox, "Also delete the downloaded files from the disk", "the files option").AsCheckBox().IsChecked = true;
        dialog.Press("Delete");

        UiHelpers.WaitFor(() => File.Exists(path) ? null : "gone", "the file to be deleted", window);
        UiHelpers.WaitFor(() => window.RowTexts().Count == 0 ? "empty" : null, "the list to be empty", window);
    }

    [UiInputFact]
    [Trait("TC", "TC-UI-002")]
    [Trait("Requirement", "KASI-UI-003")]
    public async Task A_double_click_opens_the_details_window_with_its_buttons_and_Close_closes_it()
    {
        await using var server = await TestServerHost.StartAsync(cancellationToken: Ct);
        await using var app = AppProcess.Start();
        var window = app.WaitForWindow(AppProcess.MainWindowName);
        window.AddDownload(app, new Uri(server.BaseAddress, "/files/ui-details/34359738368").ToString(), "details.bin"); // 32 GiB: still running
        var row = UiHelpers.WaitFor(() => window.FindFirstDescendant(c => c.ByName("details.bin"))?.Ancestor(ControlType.DataItem), "the row in the list", window);

        window.SetForeground();
        row.DoubleClick();
        var details = app.WaitForWindow("Download details");

        foreach (var button in new[] { "Refresh link", "Open folder", "Pause", "Close" })
        {
            details.Named(ControlType.Button, button, "a details button");
        }

        details.Press("Close");
        WaitUntilGone(app, "Download details");
    }
}
