using System.Collections.Concurrent;

namespace Kasi.TestServer.Chaos;

/// <summary>Profiles registered by tests, in process or through <c>POST /_chaos</c>; requests select one with <c>X-Kasi-Chaos: &lt;id&gt;</c>.</summary>
public sealed class ChaosRegistry
{
    public const string HeaderName = "X-Kasi-Chaos";

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private int _lastId;
    private int _defaultRequests;

    /// <summary>A profile used when a request has no <c>X-Kasi-Chaos</c> header; tests that exercise the media runner use it.</summary>
    public ChaosProfile? DefaultProfile { get; set; }

    public string Register(ChaosProfile profile)
    {
        var id = "c" + Interlocked.Increment(ref _lastId);
        _entries[id] = new Entry(profile);
        return id;
    }

    public void Clear()
    {
        _entries.Clear();
        DefaultProfile = null;
    }

    internal ChaosSession? Begin(string? id) =>
        id is not null && _entries.TryGetValue(id, out var entry)
            ? new ChaosSession(entry.Profile, entry.NextRequestIndex())
            : DefaultProfile is { } fallback
                ? new ChaosSession(fallback, Interlocked.Increment(ref _defaultRequests) - 1)
                : null;

    private sealed class Entry(ChaosProfile profile)
    {
        private int _requests;

        public ChaosProfile Profile { get; } = profile;

        public int NextRequestIndex() => Interlocked.Increment(ref _requests) - 1;
    }
}
