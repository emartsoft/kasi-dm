using System.Text.Json;

namespace Kasi.App.Engine;

/// <summary>What "remember for this site" keeps: where downloads from one host go.</summary>
internal sealed record SiteChoice(int? CategoryId, string? Folder);

internal interface ISiteDefaults
{
    SiteChoice? Get(string host);

    Task RememberAsync(string host, SiteChoice choice, CancellationToken cancellationToken = default);
}

/// <summary>Host to choice, in one small JSON file next to the database. A missing or unreadable file is an empty memory.</summary>
internal sealed class SiteDefaultsStore : ISiteDefaults
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SiteChoice> _byHost;

    public SiteDefaultsStore(string path)
    {
        _path = path;
        _byHost = Load(path);
    }

    public SiteChoice? Get(string host)
    {
        lock (_gate)
        {
            return _byHost.GetValueOrDefault(host.ToLowerInvariant());
        }
    }

    public async Task RememberAsync(string host, SiteChoice choice, CancellationToken cancellationToken = default)
    {
        string json;
        lock (_gate)
        {
            _byHost[host.ToLowerInvariant()] = choice;
            json = JsonSerializer.Serialize(_byHost, _json);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        File.Move(temp, _path, overwrite: true);
    }

    private static Dictionary<string, SiteChoice> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, SiteChoice>>(File.ReadAllText(path), _json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}
