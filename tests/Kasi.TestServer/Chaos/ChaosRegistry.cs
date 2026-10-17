using System.Collections.Concurrent;

namespace Kasi.TestServer.Chaos;

/// <summary>Profiles registered by tests, in process or through <c>POST /_chaos</c>; requests select one with <c>X-Kasi-Chaos: &lt;id&gt;</c>.</summary>
public sealed class ChaosRegistry
{
    public const string HeaderName = "X-Kasi-Chaos";

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private int _lastId;

    public string Register(ChaosProfile profile)
    {
        var id = "c" + Interlocked.Increment(ref _lastId);
        _entries[id] = new Entry(profile);
        return id;
    }

    public void Clear() => _entries.Clear();

    internal ChaosSession? Begin(string id) =>
        _entries.TryGetValue(id, out var entry) ? new ChaosSession(entry.Profile, entry.NextRequestIndex()) : null;

    private sealed class Entry(ChaosProfile profile)
    {
        private int _requests;

        public ChaosProfile Profile { get; } = profile;

        public int NextRequestIndex() => Interlocked.Increment(ref _requests) - 1;
    }
}
