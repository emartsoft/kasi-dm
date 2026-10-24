using Kasi.App.Engine;

namespace Kasi.App.Tests;

/// <summary>M2.13c: the folder Kasi saves downloads in can be moved for a test, so a browser test never writes into the user's Downloads.</summary>
[Trait("Requirement", "KASI-BRW-001")]
public class AppPathsTests
{
    [Fact]
    public void The_downloads_folder_is_the_profile_Downloads_unless_a_test_moves_it()
    {
        var plain = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        AppPaths.DownloadsFolder(null).Should().Be(plain);
        AppPaths.DownloadsFolder("  ").Should().Be(plain);
        AppPaths.DownloadsFolder(@"D:\kasi-test\files").Should().Be(@"D:\kasi-test\files");
    }

    [Fact]
    public void The_browser_settings_file_lives_in_the_data_folder_with_the_test_suffix()
    {
        var path = Kasi.App.Browser.BrowserSettingsStore.DefaultPath("ui1a2b");

        Path.GetFileName(path).Should().Be("browser-settings-ui1a2b.json");
        Path.GetDirectoryName(path).Should().Be(AppPaths.DataFolder());
        Kasi.App.Browser.BrowserSettingsStore.DefaultPath(null).Should().EndWith("browser-settings.json");
    }
}
