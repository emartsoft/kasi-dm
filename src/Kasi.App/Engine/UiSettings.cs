using System.Text.Json;

namespace Kasi.App.Engine;

/// <summary>What the main window remembers between runs. More arrives with the layout and theme work (M2.9b).</summary>
internal sealed record UiSettings
{
    /// <summary>Ask before deleting downloads; the delete window's "Do not ask again" turns it off.</summary>
    public bool ConfirmDelete { get; init; } = true;
}

internal interface IUiSettings
{
    UiSettings Current { get; }

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

    public async Task SaveAsync(UiSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(settings, _json), cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
        _current = settings;
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
