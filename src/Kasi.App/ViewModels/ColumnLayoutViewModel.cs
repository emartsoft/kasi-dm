using Kasi.App.Engine;
using Kasi.App.Resources;

namespace Kasi.App.ViewModels;

/// <summary>A column as the grid shows it. <see cref="SortPath"/> is the row property the grid sorts it by.</summary>
internal sealed record ColumnState(string Id, string Label, string SortPath, double Width, int DisplayIndex, bool Visible);

/// <summary>
/// The grid's column layout and sort order and where they are kept (KASI-UI-007): what to show on a first run, what to restore after
/// that, and the save that follows the user's changes. Everything it hands out is checked, so an edited or old settings file cannot
/// produce a grid with no columns, an unreachable width or two columns in one place.
/// </summary>
internal sealed class ColumnLayoutViewModel(IUiSettings ui)
{
    public const double MinWidth = 40;
    public const double MaxWidth = 1200;

    /// <summary>The name column is never hidden: it is how a row is recognised, read by screen readers and opened with Enter.</summary>
    public const string NameColumn = "name";

    private static readonly (string Id, string Sort, double Width)[] _defaults =
    [
        (NameColumn, "FileName", 300),
        ("size", "SortSize", 90),
        ("progress", "Percent", 130),
        ("speed", "SortSpeed", 100),
        ("timeleft", "SortTimeLeft", 90),
        ("status", "State", 110),
        ("date", "Created", 130),
        ("category", "CategoryName", 110),
    ];

    /// <summary>Raised after <see cref="ResetAsync"/>, so the window can lay the grid out again.</summary>
    public event Action? Reset;

    public static string Label(string id) => id switch
    {
        NameColumn => Strings.JobColumnName,
        "size" => Strings.JobColumnSize,
        "progress" => Strings.JobColumnProgress,
        "speed" => Strings.JobColumnSpeed,
        "timeleft" => Strings.JobColumnTimeLeft,
        "status" => Strings.JobColumnState,
        "date" => Strings.JobColumnDateAdded,
        "category" => Strings.JobColumnCategory,
        _ => id,
    };

    /// <summary>The columns in display order: the saved layout over the defaults.</summary>
    public IReadOnlyList<ColumnState> Columns
    {
        get
        {
            var saved = ui.Current.Columns.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First());
            var merged = _defaults
                .Select((d, position) =>
                {
                    var layout = saved.GetValueOrDefault(d.Id);
                    var width = layout is { Width: >= MinWidth and <= MaxWidth } ? layout.Width : d.Width;
                    var index = layout is { DisplayIndex: >= 0 and < 1000 } ? layout.DisplayIndex : position;
                    var visible = d.Id == NameColumn || layout?.Visible != false;
                    return new ColumnState(d.Id, Label(d.Id), d.Sort, width, index, visible);
                })
                .OrderBy(c => c.DisplayIndex)
                .ThenBy(c => Array.FindIndex(_defaults, d => d.Id == c.Id))
                .Select((c, i) => c with { DisplayIndex = i }) // contiguous, so two columns never share a place
                .ToList();
            return merged;
        }
    }

    /// <summary>The sort to restore, or null.</summary>
    public (string Column, bool Descending)? Sort =>
        ui.Current.SortColumn is { } id && _defaults.Any(d => d.Id == id) ? (id, ui.Current.SortDescending) : null;

    /// <summary>Saves what the grid looks like now. The name column's width is not kept: it takes the room that is left.</summary>
    public Task SaveAsync(IEnumerable<ColumnState> columns, string? sortColumn, bool descending)
    {
        ArgumentNullException.ThrowIfNull(columns);
        var layout = columns
            .Where(c => _defaults.Any(d => d.Id == c.Id))
            .Select(c => new ColumnLayout(c.Id, Math.Clamp(c.Width, MinWidth, MaxWidth), c.DisplayIndex, c.Visible || c.Id == NameColumn))
            .ToList();
        return ui.SaveAsync(ui.Current with { Columns = layout, SortColumn = sortColumn, SortDescending = descending });
    }

    public async Task ResetAsync()
    {
        await ui.SaveAsync(ui.Current with { Columns = [], SortColumn = null, SortDescending = false }).ConfigureAwait(true);
        Reset?.Invoke();
    }
}
