using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Kasi.App.Resources;
using Kasi.App.ViewModels;

namespace Kasi.App.Views;

internal sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Grid.SelectionChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                model.SetSelection(Grid.SelectedItems.OfType<JobRowViewModel>());
            }
        };
        Grid.DoubleTapped += (_, _) =>
        {
            if (DataContext is MainWindowViewModel { SelectedRow: { } row } model)
            {
                model.OpenRowCommand.Execute(row);
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel model)
            {
                model.SearchRequested += () => SearchBox.Focus(NavigationMethod.Unspecified);
                model.Layout.Reset += ApplyLayout;
                ApplyLayout();
            }
        };

        // the grid sorts and reorders itself; what the user chose is saved after it has happened
        Grid.ColumnReordered += (_, _) => Dispatcher.UIThread.Post(SaveLayout);
        Grid.Sorting += (_, _) => Dispatcher.UIThread.Post(SaveLayout);
        Opened += (_, _) => ApplyLayout(); // the grid has its view of the rows only once it is on screen: the sort goes on then
        Closing += (_, _) => SaveLayout();
        ColumnsMenu.SubmenuOpened += (_, _) => BuildColumnsMenu();
    }

    private ColumnLayoutViewModel? Layout => (DataContext as MainWindowViewModel)?.Layout;

    private DataGridColumn? ColumnOf(string id) => Grid.Columns.FirstOrDefault(c => c.Tag as string == id);

    /// <summary>Puts the saved columns, widths and sort on the grid.</summary>
    private void ApplyLayout()
    {
        if (Layout is not { } layout)
        {
            return;
        }

        foreach (var state in layout.Columns)
        {
            if (ColumnOf(state.Id) is { } column)
            {
                column.IsVisible = state.Visible;
                if (state.Id != ColumnLayoutViewModel.NameColumn)
                {
                    column.Width = new DataGridLength(state.Width);
                }
            }
        }

        foreach (var state in layout.Columns.OrderBy(c => c.DisplayIndex))
        {
            if (ColumnOf(state.Id) is { } column && column.DisplayIndex != state.DisplayIndex)
            {
                column.DisplayIndex = state.DisplayIndex;
            }
        }

        if (Grid.CollectionView is { } view)
        {
            view.SortDescriptions.Clear();
            if (layout.Sort is { } sort && layout.Columns.FirstOrDefault(c => c.Id == sort.Column) is { } state)
            {
                view.SortDescriptions.Add(DataGridSortDescription.FromPath(state.SortPath, sort.Descending ? ListSortDirection.Descending : ListSortDirection.Ascending));
            }
        }
    }

    private void SaveLayout()
    {
        if (Layout is not { } layout)
        {
            return;
        }

        var defaults = layout.Columns.ToDictionary(c => c.Id);
        var columns = Grid.Columns
            .Where(c => c.Tag is string id && defaults.ContainsKey(id))
            .Select(c =>
            {
                var known = defaults[(string)c.Tag!];
                return known with { Width = c.ActualWidth > 0 ? c.ActualWidth : known.Width, DisplayIndex = c.DisplayIndex, Visible = c.IsVisible };
            })
            .ToList();
        var sorted = Grid.CollectionView?.SortDescriptions.FirstOrDefault();
        var sortColumn = sorted is null ? null : columns.FirstOrDefault(c => c.SortPath == sorted.PropertyPath)?.Id;
        _ = SaveAsync(layout, columns, sortColumn, sorted?.Direction == ListSortDirection.Descending);
    }

    private static async Task SaveAsync(ColumnLayoutViewModel layout, IReadOnlyList<ColumnState> columns, string? sortColumn, bool descending)
    {
        try
        {
            await layout.SaveAsync(columns, sortColumn, descending).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the layout is a convenience: the next change tries again
        }
    }

    private static async Task ResetAsync(ColumnLayoutViewModel layout)
    {
        try
        {
            await layout.ResetAsync().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the defaults could not be saved; they are still applied for this run
        }
    }

    /// <summary>"Columns": one check mark per column, and a way back to the defaults.</summary>
    private void BuildColumnsMenu()
    {
        if (Layout is not { } layout)
        {
            return;
        }

        ColumnsMenu.Items.Clear();
        foreach (var state in layout.Columns.OrderBy(c => c.DisplayIndex))
        {
            var id = state.Id;
            var item = new MenuItem
            {
                Header = state.Label,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = ColumnOf(id)?.IsVisible ?? true,
                IsEnabled = id != ColumnLayoutViewModel.NameColumn,
            };
            AutomationProperties.SetName(item, state.Label);
            item.Click += (_, _) =>
            {
                if (ColumnOf(id) is { } column)
                {
                    column.IsVisible = item.IsChecked;
                    SaveLayout();
                }
            };
            ColumnsMenu.Items.Add(item);
        }

        ColumnsMenu.Items.Add(new Separator());
        var reset = new MenuItem { Header = Strings.ColumnsReset };
        AutomationProperties.SetName(reset, Strings.ColumnsReset);
        reset.Click += (_, _) => _ = ResetAsync(layout);
        ColumnsMenu.Items.Add(reset);
    }
}
