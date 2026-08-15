using System;
using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Fleet;

public enum PeerLiveness { Online, Stale, Gone }

/// <summary>A row for the dashboard: the last status a peer sent and how fresh it is.</summary>
public sealed record FleetPeer(FleetStatus Status, DateTime LastSeenUtc, PeerLiveness Liveness, TimeSpan Age);

/// <summary>
/// The peers we have heard from, with liveness judged by <i>our</i> clock at receipt — never by
/// the sender's timestamp, which may be wrong by minutes on a box nobody has looked at.
///
/// <para>
/// Read-only by design (user decision 2026-08-15): nothing here changes what this box does. A
/// stale peer is drawn yellow, a gone peer grey and then dropped; that is the whole model.
/// </para>
/// </summary>
public sealed class FleetRoster
{
    /// <summary>After this long unheard a peer is dropped from the list entirely.</summary>
    public static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, (FleetStatus Status, DateTime Seen)> _peers = new();

    /// <summary>Record a status received now. Own frames are the caller's problem to filter (the relay never echoes them anyway).</summary>
    public void Update(FleetStatus status, DateTime nowUtc)
        => _peers[status.SenderId] = (status, nowUtc);

    /// <summary>Peers newest-first, dropping anything gone longer than <see cref="GoneAfter"/>.</summary>
    public IReadOnlyList<FleetPeer> Peers(DateTime nowUtc, TimeSpan staleAfter)
    {
        var gone = _peers.Where(kv => nowUtc - kv.Value.Seen > GoneAfter).Select(kv => kv.Key).ToList();
        foreach (var id in gone)
            _peers.Remove(id);

        return _peers.Values
            .Select(v =>
            {
                var age = nowUtc - v.Seen;
                var liveness = age <= staleAfter ? PeerLiveness.Online
                    : age <= GoneAfter ? PeerLiveness.Stale
                    : PeerLiveness.Gone;
                return new FleetPeer(v.Status, v.Seen, liveness, age);
            })
            .OrderBy(p => p.Status.Character, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int Count => _peers.Count;
}
