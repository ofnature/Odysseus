using System;
using Odysseus.Services.Ipc;

namespace Odysseus.Services.Fleet;

/// <summary>
/// Publishes this box's <see cref="FleetStatus"/> on a cadence and feeds received ones into the
/// roster. Ticked from the framework loop; does nothing when publishing is turned off, and never
/// throws into that loop.
/// </summary>
public sealed class FleetPublisher : IDisposable
{
    public const string Channel = "Odysseus.Status";
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);

    private readonly RelayIpc _relay;
    private readonly Func<FleetStatus?> _snapshot;
    private readonly Func<bool> _enabled;
    private readonly Func<DateTime> _now;
    private DateTime _lastPublish;

    public FleetRoster Roster { get; } = new();

    /// <summary>The last status we published — the dashboard's own row.</summary>
    public FleetStatus? Own { get; private set; }

    /// <param name="snapshot">Builds this box's current status; null when nothing sensible can be said (not logged in).</param>
    public FleetPublisher(RelayIpc relay, Func<FleetStatus?> snapshot, Func<bool> enabled, Func<DateTime>? now = null)
    {
        _relay = relay;
        _snapshot = snapshot;
        _enabled = enabled;
        _now = now ?? (() => DateTime.UtcNow);
        _relay.Subscribe(OnMessage);
    }

    public void Tick()
    {
        var now = _now();
        if (now - _lastPublish < Cadence)
            return;
        _lastPublish = now;

        FleetStatus? status;
        try
        {
            status = _snapshot();
        }
        catch
        {
            return;
        }
        if (status is null)
            return;

        Own = status;
        if (_enabled())
            _relay.Publish(Channel, status.ToJson());
    }

    private void OnMessage(string channel, string json)
    {
        if (channel != Channel)
            return;
        var status = FleetStatus.FromJson(json);
        if (status is null)
            return;
        // Never let a peer impersonate us on our own screen.
        if (Own is not null && status.SenderId == Own.SenderId)
            return;
        Roster.Update(status, _now());
    }

    public void Dispose() => _relay.Dispose();
}
