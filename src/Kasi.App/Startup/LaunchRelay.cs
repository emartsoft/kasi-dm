using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kasi.App.Startup;

/// <summary>
/// Carries launch requests (this process's own arguments and the ones a second launch hands over) to the UI. A request that arrives before
/// the UI exists, which is normal because the hand-off pipe opens first, waits until <see cref="Attach"/>.
/// </summary>
internal sealed partial class LaunchRelay(ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private readonly List<AppArguments> _pending = [];
    private Action<AppArguments>? _target;

    public void Submit(AppArguments request)
    {
        Action<AppArguments>? target;
        lock (_gate)
        {
            target = _target;
            if (target is null)
            {
                _pending.Add(request);
            }
        }

        target?.Invoke(request);
    }

    public void Attach(Action<AppArguments> target)
    {
        AppArguments[] waiting;
        lock (_gate)
        {
            _target = target;
            waiting = [.. _pending];
            _pending.Clear();
        }

        foreach (var request in waiting)
        {
            target(request);
        }
    }

    /// <summary>The hand-off pipe's handler: parse like <c>Main</c> does and pass it on. Only counts are logged, never a URL.</summary>
    public ValueTask OnHandOffAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var request = AppArguments.Parse(arguments);
        LogHandOff(request.Urls.Count, request.Rejected, request.Tray);
        Submit(request);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Launch hand-off received: {UrlCount} urls, {Rejected} rejected, tray {Tray}.")]
    private partial void LogHandOff(int urlCount, int rejected, bool tray);
}
