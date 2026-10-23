using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
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
    public void Main_window_opens_with_a_toolbar_a_sidebar_an_empty_job_list_and_a_status_bar_all_with_names()
    {
        var window = Open();

        window.IsVisible.Should().BeTrue();
        window.Title.Should().Be("Kasi");
        AutomationProperties.GetName(window).Should().Be("Kasi main window");
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        AutomationProperties.GetName(grid).Should().Be("Downloads");
        grid.Columns.Select(c => c.Header).Should().Equal("Name", "Size", "Progress", "Speed", "Time left", "Status", "Date added", "Category");
        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToList();
        buttons.Should().Contain(["Add URL", "Resume", "Pause", "Stop", "Delete", "Delete completed", "Start queue", "Stop queue", "Scheduler", "Settings"]);
        window.GetVisualDescendants().OfType<TextBox>().Should().Contain(t => AutomationProperties.GetName(t) == "Search downloads");
        window.GetVisualDescendants().OfType<ListBox>().Should().Contain(l => AutomationProperties.GetName(l) == "Filters");
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Should().Contain("Speed limit: Unlimited");
    }

    [AvaloniaFact]
    public void The_keyboard_shortcuts_of_the_interaction_rules_are_bound()
    {
        var window = Open();

        window.KeyBindings.Select(k => k.Gesture.ToString()).Should().Contain(["Ctrl+N", "Ctrl+F"]);
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        grid.KeyBindings.Select(k => k.Gesture.ToString()).Should().Contain(["Space", "Delete", "F5", "Return"]);
    }

    [AvaloniaFact]
    [Trait("TC", "TC-UI-007")]
    [Trait("Requirement", "KASI-UI-010")]
    public void Pressing_escape_in_search_box_clears_text_and_focuses_grid()
    {
        var window = Open();
        var searchBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Search downloads");
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();

        searchBox.Text = "testing";
        searchBox.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Escape,
        });

        searchBox.Text.Should().BeEmpty();
        grid.IsFocused.Should().BeTrue();
    }

    [AvaloniaFact]
    [Trait("TC", "TC-UI-007")]
    [Trait("Requirement", "KASI-UI-010")]
    public void Context_menu_items_have_accessible_automation_names()
    {
        var window = Open();
        var grid = window.GetVisualDescendants().OfType<DataGrid>().Single();
        grid.ContextMenu.Should().NotBeNull();
        var menuItems = grid.ContextMenu!.Items.OfType<MenuItem>().ToList();
        menuItems.Select(m => AutomationProperties.GetName(m)).Should().Contain(["Details...", "Resume", "Pause", "Delete", "Columns"]);
    }

    [AvaloniaFact]
    [Trait("Requirement", "KASI-UI-006")]
    public void Main_window_has_mica_transparency_hint_and_fallback_configured()
    {
        var window = Open();

        window.TransparencyLevelHint.Should().Contain([WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None]);
        window.TransparencyBackgroundFallback.Should().NotBeNull();
    }

    [AvaloniaFact]
    [Trait("Requirement", "KASI-UI-006")]
    public void When_mica_transparency_is_active_window_background_is_transparent()
    {
        var window = Open();

        window.OnTransparencyLevelChanged(WindowTransparencyLevel.Mica);
        window.Background.Should().Be(Brushes.Transparent);

        window.OnTransparencyLevelChanged(WindowTransparencyLevel.None);
        window.Background.Should().NotBe(Brushes.Transparent);
    }

    [AvaloniaFact]
    public void The_queue_buttons_are_shown_but_off_until_the_scheduler_exists()
    {
        var window = Open();

        var queue = window.GetVisualDescendants().OfType<Button>().Where(b => AutomationProperties.GetName(b) is "Start queue" or "Stop queue" or "Scheduler").ToList();
        queue.Should().HaveCount(3).And.OnlyContain(b => !b.IsEffectivelyEnabled);
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
