using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kasi.App.Browser;
using Kasi.App.ViewModels;
using Kasi.App.Views;
using Kasi.Data;

namespace Kasi.App.Tests;

/// <summary>M2.11c: the Categories tab in the real window (KASI-UI-010: automation names on every control).</summary>
[Trait("Category", "Integration")]
[Trait("Requirement", "KASI-UI-010")]
public sealed class CategoriesWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kasi-categories-window-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the folder is in the temp directory
        }
    }

    private sealed class FixedSettings(BrowserSettings current) : IBrowserSettings
    {
        public BrowserSettings Current { get; } = current;

#pragma warning disable CS0067
        public event Action<BrowserSettings>? Changed;
#pragma warning restore CS0067

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task The_categories_tab_shows_the_page_and_every_control_has_an_automation_name()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_root, "kasi.db"));
        await new MigrationRunner(factory).MigrateAsync(TestContext.Current.CancellationToken);
        var categories = new CategoriesViewModel(new CategoryRepository(factory), new CategoryRuleRepository(factory), @"C:\Users\test\Downloads");
        await categories.LoadAsync(TestContext.Current.CancellationToken);
        var model = new SettingsViewModel(new FixedSettings(new BrowserSettings()), null, categories);
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        model.SelectedTab = 2;
        window.UpdateLayout();

        var tabs = window.GetVisualDescendants().OfType<TabItem>().ToList();
        tabs.Select(t => AutomationProperties.GetName(t)).Should().Contain("Categories");
        var page = window.GetVisualDescendants().OfType<CategoriesView>().Single();
        var buttons = page.GetVisualDescendants().OfType<Button>().Select(b => AutomationProperties.GetName(b)).ToList();
        buttons.Should().Contain(["Save category", "Delete category", "Add category", "Move up", "Move down", "Delete rule", "Add rule", "Check"]);
        buttons.Should().OnlyContain(n => !string.IsNullOrEmpty(n));
        var boxes = page.GetVisualDescendants().OfType<TextBox>().Where(b => !b.GetVisualAncestors().OfType<ComboBox>().Any()).ToList(); // a combo box's own hidden box is not a control of the page
        boxes.Where(b => string.IsNullOrEmpty(AutomationProperties.GetName(b))).Select(b => $"[{b.PlaceholderText}|{b.Text}]").Should().BeEmpty("every text box has an automation name");
        page.GetVisualDescendants().OfType<ListBox>().Should().OnlyContain(l => !string.IsNullOrEmpty(AutomationProperties.GetName(l)));
        window.Close();
    }

    [AvaloniaFact]
    public void Without_the_category_stores_there_is_no_categories_tab()
    {
        var model = new SettingsViewModel(new FixedSettings(new BrowserSettings()));
        var window = new SettingsWindow { DataContext = model };
        window.Show();
        window.UpdateLayout();

        model.HasCategories.Should().BeFalse();
        window.GetVisualDescendants().OfType<TabItem>().Where(t => t.IsVisible).Select(t => AutomationProperties.GetName(t))
            .Should().NotContain("Categories");
        window.Close();
    }
}
