using System;
using System.Linq;
using Odysseus.Services.Deliveries;

namespace Odysseus.Services.Gathering;

/// <summary>
/// Gathering Odysseus does itself, offered to anything that would otherwise hand off.
///
/// <para>
/// Kept as an interface so <see cref="DeliveryRunner"/> takes it optionally and behaves exactly as
/// before when there is none: hand to GatherBuddy, and stop with a reason when that cannot help.
/// GatherBuddy's auto-gather lists have no notion of a collectability threshold, which is why a
/// delivery for six Glass Eye at 240 ends with it switching itself off having gathered nothing
/// useful — the failure this exists to replace.
/// </para>
/// </summary>
public interface IOwnGatherer
{
    /// <summary>
    /// Off by default. Opening a node has locked the client more than once and the cause is not
    /// understood, so nothing reaches that code without being switched on deliberately — a delivery
    /// falls back to the GatherBuddy handoff exactly as it did before.
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>There is a node with coordinates for this item, so it is worth trying.</summary>
    bool CanGather(uint itemId);

    /// <summary>Why <see cref="CanGather"/> said no, for the log: which link is missing.</summary>
    string WhyNot(uint itemId);

    /// <summary>Go and get <paramref name="count"/> of it at <paramref name="collectability"/> or better.</summary>
    bool Start(uint itemId, int count, int collectability);

    /// <summary>Drive it. Whatever started it is responsible for ticking it.</summary>
    void Tick();

    bool Busy { get; }

    /// <summary>Gave up. <see cref="Status"/> says why.</summary>
    bool Faulted { get; }

    string Status { get; }

    /// <summary>Walk it all through without touching a node. See <see cref="GatherRunner.DryRun"/>.</summary>
    bool DryRun { get; set; }

    /// <summary>Open one node, report what the window says, and stop. See <see cref="GatherRunner.ProbeOnly"/>.</summary>
    bool ProbeOnly { get; set; }

    void Stop();
}

/// <summary>
/// <see cref="GatherRunner"/> behind <see cref="IOwnGatherer"/>: resolve the item to a node, start
/// the runner on it, and report how it is doing.
/// </summary>
public sealed class OwnGatherer : IOwnGatherer
{
    private readonly GatherRunner _runner;
    private readonly IGatheringSource _source;
    private readonly NodeAtlas _atlas;
    private readonly Action<string> _log;

    public OwnGatherer(GatherRunner runner, IGatheringSource source, NodeAtlas atlas, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(atlas);

        _runner = runner;
        _source = source;
        _atlas = atlas;
        _log = log;
    }

    public bool Enabled { get; set; }

    public bool CanGather(uint itemId)
        => Enabled && GatheringPlan.For(itemId, _source, _atlas) is not null;

    public string WhyNot(uint itemId)
    {
        var points = _source.PointsFor(itemId);
        if (points.Count == 0)
            return $"the sheets name no gathering point for item {itemId}";
        var placed = points.Count(p => p.HasZone);
        var withSpawns = points.Count(p => _atlas.SpawnsOf(p.NodeId).Count > 0);
        return $"item {itemId} has {points.Count} point(s) in the sheets, {placed} placed in a zone, " +
               $"{withSpawns} with atlas spawns";
    }

    public bool Start(uint itemId, int count, int collectability)
    {
        // Checked here as well as in CanGather: a caller that asks directly must not get through
        // either, or "off" is only off for the callers that thought to ask first.
        if (!Enabled)
            return false;

        var targets = GatheringPlan.All(itemId, _source, _atlas);
        if (targets.Count == 0)
        {
            _log($"Nothing in the node atlas yields item {itemId}.");
            return false;
        }

        var first = targets[0];
        var usable = targets.Where(t => t.TerritoryId == first.TerritoryId && t.ClassJobId == first.ClassJobId).ToList();
        _log($"Gathering {count} × {itemId} at {collectability}+ in territory {first.TerritoryId} — " +
             $"{usable.Count} node(s), {usable.Sum(t => t.Spawns.Count)} stop(s).");
        _runner.Begin(usable, count, collectability);
        return true;
    }

    public void Tick() => _runner.Tick();

    /// <summary>
    /// Anything that is not finished. Written as the inverse on purpose: listing the busy states
    /// meant adding one to the enum and forgetting it here, and a runner reported idle the instant
    /// it began reads to its caller as having given up before it moved.
    /// </summary>
    public bool Busy => _runner.State is not (GatherRunState.Idle or GatherRunState.Done or GatherRunState.Faulted);

    public bool Faulted => _runner.State == GatherRunState.Faulted;

    public string Status => _runner.State == GatherRunState.Faulted ? _runner.FailReason : _runner.Status;

    public bool DryRun { get => _runner.DryRun; set => _runner.DryRun = value; }

    public bool ProbeOnly { get => _runner.ProbeOnly; set => _runner.ProbeOnly = value; }

    public void Stop() => _runner.Cancel();
}
