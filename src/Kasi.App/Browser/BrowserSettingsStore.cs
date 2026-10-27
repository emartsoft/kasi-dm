using System.Text.Json;
using System.Text.Json.Serialization;
using Kasi.App.Engine;
using Kasi.Ipc.Messages;

namespace Kasi.App.Browser;

internal interface IBrowserSettings
{
    BrowserSettings Current { get; }

    /// <summary>Raised after a change was saved, on the thread that saved it.</summary>
    event Action<BrowserSettings>? Changed;

    Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// The browser rules, kept in one small JSON file in the user's local data folder (the general settings store arrives with the Settings
/// window of a later task and takes these over). A missing or unreadable file means the defaults, never a crash at start-up; a save writes a
/// temporary file and replaces the real one, so a power cut cannot leave half a file.
/// </summary>
// CA1001: the semaphore lives as long as the process; nothing waits on it after the store stops being used
#pragma warning disable CA1001
internal sealed class BrowserSettingsStore : IBrowserSettings
#pragma warning restore CA1001
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly SemaphoreSlim _saving = new(1, 1);
    private volatile BrowserSettings _current;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BrowserSettingsStore> _shared =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One store per settings path, so the browser pipe and the Settings window see the same instance.</summary>
    public static BrowserSettingsStore ForPath(string path) => _shared.GetOrAdd(path, static value => new BrowserSettingsStore(value));

    public BrowserSettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public event Action<BrowserSettings>? Changed;

    public BrowserSettings Current => _current;

    public string FilePath => _path;

    public static string DefaultPath(string? suffix)
    {
        var name = string.IsNullOrEmpty(suffix) ? "browser-settings.json" : $"browser-settings-{suffix}.json";
        return Path.Combine(AppPaths.DataFolder(), name); // the data folder, so KASI_DATA_DIR moves it with the rest
    }

    public async Task SaveAsync(BrowserSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clean = settings.Normalized();
        await _saving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(clean, _json), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
            _current = clean;
        }
        finally
        {
            _saving.Release();
        }

        Changed?.Invoke(clean);
    }

    private static BrowserSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new BrowserSettings();
            }

            using var stream = File.OpenRead(path);
            return (JsonSerializer.Deserialize<BrowserSettings>(stream, _json) ?? new BrowserSettings()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new BrowserSettings();
        }
    }
}
