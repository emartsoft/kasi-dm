using System.Text.Json;
using System.Text.Json.Serialization;
using Kasi.Core;

namespace Kasi.App.Engine;

/// <summary>Whether the windows follow Windows' light or dark setting, or always use one of them (KASI-UI-006).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThemePreference>))]
internal enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>One grid column as the user left it (KASI-UI-007): where it sits, how wide it is, whether it shows.</summary>
internal sealed record ColumnLayout(string Id, double Width, int DisplayIndex, bool Visible);

/// <summary>What the main window remembers between runs.</summary>
internal sealed record UiSettings
{
    /// <summary>The theme override; <see cref="ThemePreference.System"/> follows Windows.</summary>
    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>The grid's columns in the user's layout; empty means the defaults.</summary>
    public IReadOnlyList<ColumnLayout> Columns { get; init; } = [];

    /// <summary>The column the grid is sorted by (a column id), or null for the engine's order.</summary>
    public string? SortColumn { get; init; }

    public bool SortDescending { get; init; }

    /// <summary>Scan each finished download through the installed antivirus (AMSI) before it gets its final name; off by default (KASI-CAT-004).</summary>
    public bool ScanDownloads { get; init; }

    /// <summary>Ask before deleting downloads; the delete window's "Do not ask again" turns it off.</summary>
    public bool ConfirmDelete { get; init; } = true;
}

internal interface IUiSettings
{
    UiSettings Current { get; }

    /// <summary>Raised after a save, on the saving thread.</summary>
    event Action<UiSettings>? Changed;

    Task SaveAsync(UiSettings settings, CancellationToken cancellationToken = default);
}

/// <summary>One small JSON file next to the database, written through a temporary file. A missing or unreadable file means the defaults.</summary>
internal sealed class UiSettingsStore : IUiSettings
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private volatile UiSettings _current;

    public UiSettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public UiSettings Current => _current;

    public event Action<UiSettings>? Changed;

    public async Task SaveAsync(UiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, _json), cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
        _current = settings;
        Changed?.Invoke(settings);
    }

    private static UiSettings Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path), _json) ?? new UiSettings() : new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiSettings();
        }
    }
}

/// <summary>The one speed limit the tray, the toolbar and the status bar all show; setting it tells the others.</summary>
internal sealed class SpeedLimitState(IJobService jobs)
{
    public long? Current => jobs.SpeedLimit;

    public event Action<long?>? Changed;

    public void Set(long? bytesPerSecond)
    {
        jobs.SpeedLimit = bytesPerSecond;
        Changed?.Invoke(bytesPerSecond);
    }
}
