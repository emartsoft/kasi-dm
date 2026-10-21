using System.Text.Json;

namespace Kasi.Bench.Model;

/// <summary>One timed download: a row of a session file (TPL 6.1.3).</summary>
public sealed record ResultRow(
    string Source,
    int Connections,
    int Run,
    string Tool,
    double Seconds,
    long Bytes,
    double Mbps,
    int PeakConnections,
    bool Sha256Ok,
    double? PeakWorkingSetMegabytes = null);

public sealed record MachineInfo(string Cpu, int LogicalCores, long MemoryMegabytes, string Os);

/// <summary>
/// A session file in <c>bench/results</c>. It records what TPL 6.1 asks for: the line speed, the time-of-day window and whether
/// the DNS cache was flushed, next to the rows.
/// </summary>
public sealed record BenchmarkSession(
    int SchemaVersion,
    DateTimeOffset StartedUtc,
    MachineInfo Machine,
    double? LineSpeedMbps,
    string Window,
    bool DnsFlushed,
    IReadOnlyDictionary<string, string> Tools,
    IReadOnlyList<ResultRow> Rows)
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static MachineInfo DescribeMachine() => new(
        Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown",
        Environment.ProcessorCount,
        GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
        System.Runtime.InteropServices.RuntimeInformation.OSDescription);

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, this, _json, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<BenchmarkSession> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var session = await JsonSerializer.DeserializeAsync<BenchmarkSession>(stream, _json, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The session file is empty.");
            return session.SchemaVersion == CurrentSchemaVersion
                ? session
                : throw new InvalidDataException($"Unsupported schema version {session.SchemaVersion}.");
        }
    }
}
