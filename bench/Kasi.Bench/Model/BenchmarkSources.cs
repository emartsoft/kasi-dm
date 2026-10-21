using System.Text.Json;

namespace Kasi.Bench.Model;

/// <summary>One entry of <c>bench/sources.json</c> (TPL 6.2).</summary>
public sealed record BenchmarkSource(
    string Id,
    string Type,
    string Url,
    long? SizeBytes,
    string Purpose,
    string Owner,
    bool Placeholder);

/// <summary>A source served by <c>Kasi.TestServer</c>, so the harness runs end to end without the owner's servers.</summary>
public sealed record LocalSource(string Id, string Type, string Route, long SizeBytes, string Purpose);

/// <summary>The parsed <c>bench/sources.json</c>: the public/owner sources and the local TestServer equivalents.</summary>
public sealed record BenchmarkSourceFile(IReadOnlyList<BenchmarkSource> Sources, IReadOnlyList<LocalSource> Local)
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>Sources that are real URLs. Placeholders are skipped with a message and never fetched.</summary>
    public IEnumerable<BenchmarkSource> Runnable => Sources.Where(s => !s.Placeholder);

    public static async Task<BenchmarkSourceFile> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var file = await JsonSerializer.DeserializeAsync<BenchmarkSourceFile>(stream, _json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The sources file is empty.");
            file.Validate();
            return file;
        }
    }

    /// <summary>No source may carry a query string, fragment or credentials (CLAUDE.md rule 3); ids must be unique.</summary>
    public void Validate()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !seen.Add(source.Id))
            {
                throw new InvalidDataException($"Source id '{source.Id}' is empty or repeated.");
            }

            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                throw new InvalidDataException($"Source '{source.Id}' needs an absolute http or https URL.");
            }

            if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            {
                throw new InvalidDataException($"Source '{source.Id}' must not have a query string, fragment or credentials in its URL.");
            }
        }

        foreach (var local in Local)
        {
            if (!seen.Add(local.Id) || !local.Route.StartsWith('/') || local.SizeBytes <= 0)
            {
                throw new InvalidDataException($"Local source '{local.Id}' is repeated or malformed.");
            }
        }
    }
}
