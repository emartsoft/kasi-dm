using System.Net.NetworkInformation;

namespace Kasi.Engine;

/// <summary>
/// Production <see cref="INetworkStatus"/>. NetworkChange can fire several times for one adapter flap or VPN reconnect, so
/// the new state is only reported after it has stayed stable for <see cref="Debounce"/>.
/// </summary>
public sealed class SystemNetworkStatus : INetworkStatus, IDisposable
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private CancellationTokenSource? _pending;
    private bool _online;
    private bool _disposed;

    public SystemNetworkStatus(TimeProvider? timeProvider = null, TimeSpan? debounce = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        Debounce = debounce ?? TimeSpan.FromSeconds(1);
        _online = Read();
        NetworkChange.NetworkAvailabilityChanged += OnChanged;
        NetworkChange.NetworkAddressChanged += OnChanged;
    }

    public TimeSpan Debounce { get; }

    public bool IsOnline => Volatile.Read(ref _online);

    public event EventHandler? OnlineChanged;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnChanged;
        NetworkChange.NetworkAddressChanged -= OnChanged;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private static bool Read()
    {
        try
        {
            return NetworkInterface.GetIsNetworkAvailable();
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var next = Read();
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            cancellation = new CancellationTokenSource();
            _pending = cancellation;
        }

        _ = DebounceAsync(next, cancellation);
    }

    private async Task DebounceAsync(bool next, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(Debounce, _time, cancellation.Token).ConfigureAwait(false);
            if (IsOnline == next)
            {
                return;
            }

            Volatile.Write(ref _online, next);
            OnlineChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // a newer flap superseded this one
        }
    }
}
