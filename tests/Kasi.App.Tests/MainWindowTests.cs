using Avalonia;
using Avalonia.Automation;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-UI-010")]
public class MainWindowTests
{
    private static MainWindow Open()
    {
        using var host = CompositionRoot.Build([]);
        var window = new MainWindow { DataContext = host.Services.GetRequiredService<MainWindowViewModel>() };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void Main_window_opens_with_an_empty_job_list_a_title_and_an_automation_name()
    {
        var window = Open();

        window.IsVisible.Should().BeTrue();
        window.Title.Should().Be("Kasi");
        AutomationProperties.GetName(window).Should().Be("Kasi main window");
        var list = window.Content.Should().BeOfType<Avalonia.Controls.DataGrid>().Subject;
        AutomationProperties.GetName(list).Should().Be("Downloads");
        list.Columns.Select(c => c.Header).Should().Equal("Name", "Status", "Progress", "Size");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void Main_window_renders_in_both_theme_variants(string variant)
    {
        var theme = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        Application.Current!.RequestedThemeVariant = theme;

        var window = Open();

        window.ActualThemeVariant.Should().Be(theme);
        Application.Current.TryGetResource("ButtonBackground", theme, out var resource).Should().BeTrue("the Fluent control resources must be loaded exactly once");
        resource.Should().NotBeNull();
    }
}
