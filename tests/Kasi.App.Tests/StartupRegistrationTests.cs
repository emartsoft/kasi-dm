using Kasi.App.Startup;
using Microsoft.Win32;

namespace Kasi.App.Tests;

[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-SYS-003")]
[Trait("TC", "TC-SYS-004")]
public sealed class StartupRegistrationTests
{
    [Fact]
    public void The_run_entry_is_added_once_and_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = @"Software\KasiTests\" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(path)!;
        try
        {
            var registration = new WindowsStartupRegistration(@"C:\Apps\Kasi.exe", root, "Run");
            registration.IsEnabled().Should().BeFalse();

            registration.SetEnabled(true);
            registration.IsEnabled().Should().BeTrue();
            using (var run = root.OpenSubKey("Run")!)
            {
                run.GetValue(WindowsStartupRegistration.ValueName).Should().Be("\"C:\\Apps\\Kasi.exe\" --tray");
            }

            registration.SetEnabled(true);
            using (var run = root.OpenSubKey("Run")!)
            {
                run.GetValueNames().Should().ContainSingle(name => name == WindowsStartupRegistration.ValueName);
            }

            registration.SetEnabled(false);
            registration.IsEnabled().Should().BeFalse();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
