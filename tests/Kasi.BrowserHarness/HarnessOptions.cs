using System.Text.Json;
using Kasi.Ipc.Messages;

namespace Kasi.BrowserHarness;

/// <summary>
/// What the harness is asked to do. <c>serve</c> reads it from <c>--config &lt;file&gt;</c>; <c>--tray</c> (the harness running as
/// <c>Kasi.exe</c>, started by the native host) reads <c>kasi-harness.json</c> from its own folder.
/// </summary>
internal sealed record HarnessOptions
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    /// <summary>False: serve the test pages and the test server only, with no Kasi pipe (the stub is started later by <c>--tray</c>).</summary>
    public bool Stub { get; init; } = true;

    /// <summary>The admin and page server's port; 0 picks a free one.</summary>
    public int AdminPort { get; init; }

    /// <summary>Tray only: how long to wait before opening the pipe, so the host has to retry its connection.</summary>
    public int LaunchDelayMs { get; init; }

    /// <summary>What the stub does with an <c>addDownload</c>: <c>ack</c>, <c>error</c> or <c>silent</c>.</summary>
    public string Mode { get; init; } = "ack";

    /// <summary>Wait this long before answering in <c>ack</c> mode.</summary>
    public int AnswerDelayMs { get; init; }

    public RulesOptions Rules { get; init; } = new();

    public static HarnessOptions Load(string path) =>
        JsonSerializer.Deserialize<HarnessOptions>(File.ReadAllText(path), _json) ?? throw new InvalidDataException("Empty harness config.");
}

internal sealed record RulesOptions
{
    public bool Enabled { get; init; } = true;

    public string[] Extensions { get; init; } = [];

    public string[] MimeTypes { get; init; } = ["application/octet-stream"];

    public string[] ExcludedSites { get; init; } = [];

    public BypassKey BypassKey { get; init; } = BypassKey.Alt;

    public int AckTimeoutMs { get; init; } = 2000;

    public Rules ToMessage(string id) => new()
    {
        Id = id,
        Enabled = Enabled,
        Extensions = Extensions,
        MimeTypes = MimeTypes,
        ExcludedSites = ExcludedSites,
        BypassKey = BypassKey,
        AckTimeoutMs = AckTimeoutMs,
    };
}
