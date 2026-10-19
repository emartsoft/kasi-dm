namespace Kasi.Engine;

/// <summary>
/// The engine's view of network reachability. Production uses a debounced NetworkChange implementation; tests use a fake.
/// </summary>
public interface INetworkStatus
{
    bool IsOnline { get; }

    event EventHandler? OnlineChanged;
}
