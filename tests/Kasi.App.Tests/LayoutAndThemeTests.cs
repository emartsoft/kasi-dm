using System.Collections.Specialized;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Kasi.App.Browser;
using Kasi.App.Engine;
using Kasi.App.ViewModels;
using Kasi.App.Views;

namespace Kasi.App.Tests;

[Trait("Requirement", "KASI-UI-007")]
public class ColumnLayoutViewModelTests
{
    private static ColumnLayoutViewModel With(params ColumnLayout[] saved)
    {
        var ui = new MemoryUiSettings();
        ui.Set(ui.Current with { Columns = saved });
        return new ColumnLayoutViewModel(ui);
    }

    [Fact]
    public void A_first_run_shows_the_eight_columns_of_the_wireframe_in_order()
    {
        var columns = new ColumnLayoutViewModel(new MemoryUiSettings()).Columns;

        columns.Select(c => c.Id).Should().Equal("name", "size", "progress", "speed", "timeleft", "status", "date", "category");
        columns.Select(c => c.DisplayIndex).Should().Equal(0, 1, 2, 3, 4, 5, 6, 7);
        columns.Should().OnlyContain(c => c.Visible);
        columns.Select(c => c.Label).Should().Equal("Name", "Size", "Progress", "Speed", "Time left", "Status", "Date added", "Category");
    }

    [Fact]
    public void A_saved_layout_is_restored_widths_order_and_hidden_columns()
    {
        var layout = With(new ColumnLayout("category", 150, 0, true), new ColumnLayout("name", 300, 1, true), new ColumnLayout("size", 200, 2, true), new ColumnLayout("progress", 130, 3, true), new ColumnLayout("speed", 60, 9, false), new ColumnLayout("timeleft", 90, 4, true), new ColumnLayout("status", 110, 5, true), new ColumnLayout("date", 130, 6, true));

        var columns = layout.Columns;

        columns[0].Id.Should().Be("category");
        columns[1].Id.Should().Be("name");
        columns.Single(c => c.Id == "size").Width.Should().Be(200);
        columns.Single(c => c.Id == "speed").Visible.Should().BeFalse();
        columns.Select(c => c.DisplayIndex).Should().Equal(Enumerable.Range(0, 8), "places are contiguous whatever was saved");
    }

    [Fact]
    public void The_name_column_cannot_be_hidden()
    {
        var layout = With(new ColumnLayout("name", 300, 0, false));

        layout.Columns.Single(c => c.Id == "name").Visible.Should().BeTrue();
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(99999.0)]
    [InlineData(double.NaN)]
    public void A_width_that_makes_no_sense_falls_back_to_the_default(double width)
    {
        var layout = With(new ColumnLayout("size", width, 1, true));

        layout.Columns.Single(c => c.Id == "size").Width.Should().Be(90);
    }

    [Fact]
    public void Unknown_columns_and_duplicates_in_the_file_are_ignored()
    {
        var layout = With(new ColumnLayout("bogus", 100, 0, true), new ColumnLayout("size", 120, 3, true), new ColumnLayout("size", 500, 0, true));

        layout.Columns.Should().HaveCount(8).And.OnlyContain(c => c.Id != "bogus");
        layout.Columns.Single(c => c.Id == "size").Width.Should().Be(120, "the first entry for a column wins");
    }

    [Fact]
    public async Task Saving_keeps_what_the_grid_looks_like_and_clamps_what_could_not_be_shown()
    {
        var ui = new MemoryUiSettings();
        var layout = new ColumnLayoutViewModel(ui);
        var grid = layout.Columns.Select(c => c.Id == "size" ? c with { Width = 5000 } : c.Id == "name" ? c with { Visible = false } : c).ToList();

        await layout.SaveAsync(grid, "size", descending: true);

        ui.Current.Columns.Single(c => c.Id == "size").Width.Should().Be(ColumnLayoutViewModel.MaxWidth);
        ui.Current.Columns.Single(c => c.Id == "name").Visible.Should().BeTrue();
        ui.Current.SortColumn.Should().Be("size");
        ui.Current.SortDescending.Should().BeTrue();
        layout.Sort.Should().Be(("size", true));
    }

    [Fact]
    public void A_sort_on_a_column_that_does_not_exist_is_ignored()
    {
        var ui = new MemoryUiSettings();
        ui.Set(ui.Current with { SortColumn = "bogus" });

        new ColumnLayoutViewModel(ui).Sort.Should().BeNull();
    }

    [Fact]
    public async Task Reset_goes_back_to_the_defaults_and_tells_the_window()
    {
        var ui = new MemoryUiSettings();
        var layout = new ColumnLayoutViewModel(ui);
        await layout.SaveAsync(layout.Columns.Select(c => c with { Width = 500 }), "speed", false);
        var resets = 0;
        layout.Reset += () => resets++;

        await layout.ResetAsync();

        ui.Current.Columns.Should().BeEmpty();
        layout.Sort.Should().BeNull();
        layout.Columns.Single(c => c.Id == "size").Width.Should().Be(90);
        resets.Should().Be(1);
    }
}

[Trait("Requirement", "KASI-UI-007")]
public class UiSettingsStoreTests
{
    private static string NewPath() => Path.Combine(Path.GetTempPath(), "kasi-ui-" + Guid.NewGuid().ToString("N"), "ui-settings.json");

    [Fact]
    public async Task The_layout_the_sort_and_the_theme_survive_a_restart()
    {
        var path = NewPath();
        var first = new UiSettingsStore(path);
        await first.SaveAsync(first.Current with
        {
            Theme = ThemePreference.Dark,
            Columns = [new ColumnLayout("size", 140, 2, false)],
            SortColumn = "date",
            SortDescending = true,
            ConfirmDelete = false,
        }, TestContext.Current.CancellationToken);

        var second = new UiSettingsStore(path).Current;

        second.Theme.Should().Be(ThemePreference.Dark);
        second.Columns.Should().ContainSingle().Which.Should().Be(new ColumnLayout("size", 140, 2, false));
        second.SortColumn.Should().Be("date");
        second.SortDescending.Should().BeTrue();
        second.ConfirmDelete.Should().BeFalse();
        (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Should().Contain("\"Dark\"", "the theme is stored by name, so the file reads and survives a reordering of the enum");
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void A_missing_broken_or_old_file_gives_the_defaults()
    {
        var folder = Path.GetDirectoryName(NewPath())!;
        Directory.CreateDirectory(folder);
        var broken = Path.Combine(folder, "broken.json");
        File.WriteAllText(broken, "{ not json");
        var old = Path.Combine(folder, "old.json");
        File.WriteAllText(old, "{ \"confirmDelete\": false }"); // written before the layout and the theme existed

        new UiSettingsStore(Path.Combine(folder, "missing.json")).Current.Should().Be(new UiSettings());
        new UiSettingsStore(broken).Current.Should().Be(new UiSettings());
        var loaded = new UiSettingsStore(old).Current;
        loaded.ConfirmDelete.Should().BeFalse();
        loaded.Theme.Should().Be(ThemePreference.System);
        loaded.Columns.Should().BeEmpty();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task A_save_tells_the_listeners()
    {
        var path = NewPath();
        var store = new UiSettingsStore(path);
        UiSettings? seen = null;
        store.Changed += changed => seen = changed;

        await store.SaveAsync(store.Current with { Theme = ThemePreference.Light }, TestContext.Current.CancellationToken);

        seen!.Theme.Should().Be(ThemePreference.Light);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}

[Trait("Requirement", "KASI-UI-006")]
public class ThemeTests
{
    private sealed class FixedBrowserSettings : IBrowserSettings
    {
        public BrowserSettings Current { get; } = new();

#pragma warning disable CS0067
        public event Action<BrowserSettings>? Changed;
#pragma warning restore CS0067

        public Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Theory]
    [InlineData((int)ThemePreference.System)]
    [InlineData((int)ThemePreference.Light)]
    [InlineData((int)ThemePreference.Dark)]
    public void The_preference_maps_to_the_variant_and_system_means_default_which_follows_windows(int value)
    {
        var preference = (ThemePreference)value;
        var expected = preference switch
        {
            ThemePreference.Light => ThemeVariant.Light,
            ThemePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        ThemeApplier.Variant(preference).Should().Be(expected);
    }

    [AvaloniaFact]
    [Trait("TC", "TC-UI-005")]
    public void Applying_a_preference_sets_the_requested_variant_and_system_goes_back_to_following_windows()
    {
        var app = Application.Current!;

        ThemeApplier.Apply(app, ThemePreference.Dark);
        app.RequestedThemeVariant.Should().Be(ThemeVariant.Dark);
        app.ActualThemeVariant.Should().Be(ThemeVariant.Dark);
        ThemeApplier.Apply(app, ThemePreference.Light);
        app.ActualThemeVariant.Should().Be(ThemeVariant.Light);
        ThemeApplier.Apply(app, ThemePreference.System);

        app.RequestedThemeVariant.Should().Be(ThemeVariant.Default);
    }

    [Fact]
    [Trait("TC", "TC-UI-005")]
    public async Task The_override_chosen_in_settings_is_saved_and_a_new_run_finds_it()
    {
        var path = Path.Combine(Path.GetTempPath(), "kasi-theme-" + Guid.NewGuid().ToString("N"), "ui-settings.json");
        var ui = new UiSettingsStore(path);
        var form = new SettingsViewModel(new FixedBrowserSettings(), ui);
        form.SelectedTheme.Preference.Should().Be(ThemePreference.System, "the default follows Windows");

        form.SelectedTheme = form.Themes.Single(t => t.Preference == ThemePreference.Dark);
        await form.SaveCommand.ExecuteAsync(null);

        new UiSettingsStore(path).Current.Theme.Should().Be(ThemePreference.Dark);
        new SettingsViewModel(new FixedBrowserSettings(), new UiSettingsStore(path)).SelectedTheme.Preference.Should().Be(ThemePreference.Dark);
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void The_choices_are_named_for_the_user()
    {
        var form = new SettingsViewModel(new FixedBrowserSettings(), new MemoryUiSettings());

        form.Themes.Select(t => t.Label).Should().Equal("Follow Windows", "Light", "Dark");
    }
}

public class BulkObservableCollectionTests
{
    private sealed class Item(int id)
    {
        public int Id { get; } = id;
    }

    private static List<NotifyCollectionChangedAction> Watch(BulkObservableCollection<Item> collection)
    {
        var actions = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, e) => actions.Add(e.Action);
        return actions;
    }

    [Fact]
    public void A_large_change_is_one_reset_and_the_content_is_exact()
    {
        var items = Enumerable.Range(0, 500).Select(i => new Item(i)).ToList();
        var collection = new BulkObservableCollection<Item>();
        var actions = Watch(collection);

        collection.ReplaceAll(items);

        actions.Should().Equal(NotifyCollectionChangedAction.Reset);
        collection.Select(i => i.Id).Should().Equal(Enumerable.Range(0, 500));
    }

    [Fact]
    public void The_same_content_raises_nothing()
    {
        var items = Enumerable.Range(0, 5).Select(i => new Item(i)).ToList();
        var collection = new BulkObservableCollection<Item>();
        collection.ReplaceAll(items);
        var actions = Watch(collection);

        collection.ReplaceAll(items);

        actions.Should().BeEmpty();
    }

    [Fact]
    public void A_few_changes_are_single_changes_so_a_selection_in_the_grid_survives_them()
    {
        var items = Enumerable.Range(0, 10).Select(i => new Item(i)).ToList();
        var collection = new BulkObservableCollection<Item>();
        collection.ReplaceAll(items);
        var actions = Watch(collection);

        collection.ReplaceAll([items[0], items[1], items[3], items[4], items[5], items[6], items[7], items[8], items[9], new Item(10)]);

        actions.Should().NotContain(NotifyCollectionChangedAction.Reset);
        collection.Select(i => i.Id).Should().Equal(0, 1, 3, 4, 5, 6, 7, 8, 9, 10);
    }

    [Fact]
    public void A_change_in_the_middle_keeps_the_order_of_the_new_content()
    {
        var items = Enumerable.Range(0, 6).Select(i => new Item(i)).ToList();
        var collection = new BulkObservableCollection<Item>();
        collection.ReplaceAll(items);
        var added = new Item(99);

        collection.ReplaceAll([items[0], items[1], added, items[2], items[5]]);

        collection.Select(i => i.Id).Should().Equal(0, 1, 99, 2, 5);
    }

    [Fact]
    public void A_reordering_is_a_reset_because_the_single_changes_assume_the_order_stays()
    {
        var items = Enumerable.Range(0, 4).Select(i => new Item(i)).ToList();
        var collection = new BulkObservableCollection<Item>();
        collection.ReplaceAll(items);

        collection.ReplaceAll([items[3], items[2], items[1], items[0]]);

        collection.Select(i => i.Id).Should().Equal(3, 2, 1, 0);
    }
}

[Trait("Requirement", "KASI-UI-007")]
public class MainWindowLayoutTests
{
    private static DataGridColumn Column(DataGrid grid, string id) => grid.Columns.Single(c => c.Tag as string == id);

    [AvaloniaFact]
    [Trait("TC", "TC-UI-005")]
    public async Task The_window_restores_the_saved_columns_widths_order_and_sort()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip", total: 5000), TestJobs.New("b.zip", total: 100));
        await rig.Ui.SaveAsync(rig.Ui.Current with
        {
            Columns = [new ColumnLayout("category", 150, 0, true), new ColumnLayout("name", 300, 1, true), new ColumnLayout("size", 200, 2, true), new ColumnLayout("progress", 130, 3, true), new ColumnLayout("speed", 60, 7, false), new ColumnLayout("timeleft", 90, 4, true), new ColumnLayout("status", 110, 5, true), new ColumnLayout("date", 130, 6, true)],
            SortColumn = "size",
            SortDescending = true,
        });
        var window = new MainWindow { DataContext = rig.Model };

        window.Show();

        var grid = window.FindControl<DataGrid>("Grid")!;
        Column(grid, "speed").IsVisible.Should().BeFalse();
        Column(grid, "category").DisplayIndex.Should().Be(0);
        Column(grid, "size").Width.Value.Should().Be(200);
        grid.CollectionView!.SortDescriptions.Should().ContainSingle().Which.PropertyPath.Should().Be("SortSize");
        grid.CollectionView.Cast<JobRowViewModel>().Select(r => r.FileName).Should().Equal("a.zip", "b.zip");
    }

    [AvaloniaFact]
    [Trait("TC", "TC-UI-005")]
    public async Task Closing_the_window_saves_what_the_grid_looks_like()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip"));
        var window = new MainWindow { DataContext = rig.Model };
        window.Show();
        var grid = window.FindControl<DataGrid>("Grid")!;
        Column(grid, "speed").IsVisible = false;
        Column(grid, "category").DisplayIndex = 1;

        window.Close();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        rig.Ui.Current.Columns.Single(c => c.Id == "speed").Visible.Should().BeFalse();
        rig.Ui.Current.Columns.Single(c => c.Id == "category").DisplayIndex.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task The_sort_survives_a_filter_change_because_the_list_object_stays_the_same()
    {
        await using var rig = await MainRig.CreateAsync(TestJobs.New("a.zip", total: 100), TestJobs.New("b.zip", total: 900), TestJobs.New("c.pdf", total: 500));
        var window = new MainWindow { DataContext = rig.Model };
        window.Show();
        var grid = window.FindControl<DataGrid>("Grid")!;
        grid.CollectionView!.SortDescriptions.Add(DataGridSortDescription.FromPath("SortSize", System.ComponentModel.ListSortDirection.Descending));

        rig.List.SearchText = "zip";

        grid.CollectionView.SortDescriptions.Should().ContainSingle();
        grid.CollectionView.Cast<JobRowViewModel>().Select(r => r.FileName).Should().Equal("b.zip", "a.zip");
    }
}
