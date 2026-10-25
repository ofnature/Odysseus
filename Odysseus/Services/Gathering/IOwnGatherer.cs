using System;

namespace Odysseus.Services.Gathering;

/// <summary>
/// Gathering done for Odysseus — by Demeter since 2026-10-22 (<see cref="DemeterGatherer"/>), offered
/// to anything that would otherwise hand off.
///
/// <para>
/// Kept as an interface so <see cref="Deliveries.DeliveryRunner"/> takes it optionally and behaves exactly as
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
    bool CanGather(uint itemId, uint territoryHint = 0);

    /// <summary>Why <see cref="CanGather"/> said no, for the log: which link is missing.</summary>
    string WhyNot(uint itemId, uint territoryHint = 0);

    /// <summary>The zone the gatherer would go to for this item, or null when it cannot place it.</summary>
    uint? ZoneOf(uint itemId);

    /// <summary>Where the item is found, for people: "Yak T'el — Miner Lv 90". Empty when the sheets do not say.</summary>
    string Where(uint itemId);

    /// <summary>"up · 4:12 left" or "in 12:34" for an item only timed nodes yield; empty otherwise.</summary>
    string Timer(uint itemId, DateTime utc) => string.Empty;

    /// <summary>
    /// For an item only timed nodes yield: zero when it can be gathered now, else the wait until it
    /// can. Null when the item is not timed-only — the ordinary <see cref="CanGather"/> applies.
    /// </summary>
    TimeSpan? WaitFor(uint itemId, DateTime utc) => null;

    /// <summary>At a node — opening it, working it, or closing its window. Stopping now would waste it.</summary>
    bool AtNode => false;

    /// <summary>For a timed-only item whose window is up: how long it stays up. Null otherwise.</summary>
    TimeSpan? UpFor(uint itemId, DateTime utc) => null;

    /// <summary>Go and get <paramref name="count"/> of it at <paramref name="collectability"/> or better.</summary>
    bool Start(uint itemId, int count, int collectability, uint territoryHint = 0);

    /// <summary>Drive it. Whatever started it is responsible for ticking it.</summary>
    void Tick();

    bool Busy { get; }

    /// <summary>Gave up. <see cref="Status"/> says why.</summary>
    bool Faulted { get; }

    string Status { get; }

    /// <summary>Walk it all through without touching a node (a bench mode; Demeter's debug window has it now).</summary>
    bool DryRun { get; set; }

    /// <summary>Open one node, report what the window says, and stop (a bench mode, as <see cref="DryRun"/>).</summary>
    bool ProbeOnly { get; set; }

    void Stop();
}
