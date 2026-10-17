using System.Collections.Concurrent;

namespace Kasi.TestServer;

/// <summary>State behind <c>/mutable/{id}</c>: each <see cref="Change"/> bumps the version, which changes the bytes and the ETag.</summary>
public sealed class MutableFiles
{
    public const long DefaultSize = 1024 * 1024;

    private readonly ConcurrentDictionary<string, (int Version, long Size)> _files = new();

    public (int Version, long Size) Get(string id) => _files.GetValueOrDefault(id, (0, DefaultSize));

    /// <summary>Changes the content (and optionally the size) of <paramref name="id"/> and returns the new version.</summary>
    public int Change(string id, long? size = null) =>
        _files.AddOrUpdate(id, (1, size ?? DefaultSize), (_, current) => (current.Version + 1, size ?? current.Size)).Version;
}
