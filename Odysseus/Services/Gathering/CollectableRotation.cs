namespace Odysseus.Services.Gathering;

/// <summary>What to do next at an open collectable node.</summary>
public enum GatherMove
{
    /// <summary>Bank the item at the collectability it has now. Costs a point of integrity too.</summary>
    Collect,

    /// <summary>Raise collectability by the full amount your gathering rating gives.</summary>
    Scour,

    /// <summary>Raise by a random 50% to 150% of Scour.</summary>
    Brazen,

    /// <summary>Raise by 75% of Scour, with a chance of costing no integrity at all.</summary>
    Meticulous,

    /// <summary>Spend GP to improve the next raise.</summary>
    Scrutiny,
}

/// <summary>What the open node and the character look like right now.</summary>
/// <param name="Collectability">What the item would be worth if collected this instant.</param>
/// <param name="Target">The collectability wanted.</param>
/// <param name="IntegrityLeft">Attempts remaining, of which the Collect itself takes one.</param>
/// <param name="Gp">Gathering points available.</param>
/// <param name="ScrutinyUsed">Scrutiny is already improving the next raise.</param>
/// <param name="GpReserve">GP to leave untouched.</param>
/// <param name="ScourYield">What the window says a Scour is worth here; 0 when not read.</param>
/// <param name="MeticulousYield">What the window says a Meticulous is worth; 0 when not read.</param>
/// <param name="Minimum">The floor worth banking at all — for a delivery, the same as the target.</param>
public readonly record struct CollectableState(
    int Collectability, int Target, int IntegrityLeft, int Gp, bool ScrutinyUsed, int GpReserve = 0,
    int ScourYield = 0, int MeticulousYield = 0, int Minimum = 0);

/// <summary>
/// Which collectable action to use next.
///
/// <para>
/// The window states what each action is worth — Scour's number, Meticulous's number, and the
/// chance Meticulous costs no integrity — so this reads them rather than guessing, and the
/// stopping condition is measured either way: collectability is read every step and the item is
/// collected the moment it clears.
/// </para>
///
/// <para>
/// The mechanics, from the game's own tooltips (2026-08-22): <b>Scour</b> raises by the full
/// amount the gathering rating gives. <b>Meticulous</b> raises by 75% of Scour but has a chance —
/// set by the gathering stat — of not costing integrity at all. <b>Collect itself costs a
/// point</b>, which shapes everything: integrity is items, GP comes back on its own.
/// </para>
///
/// <para>
/// The order is GatherBuddy's own collectable rotation (<c>AutoGather.Collectables.cs</c>,
/// 2026-08-22), rule for rule: collect at the target; on the last point, collect only what is
/// worth banking; Scrutiny when no single action would finish; Meticulous as both the preferred
/// finisher and the default raise, for its free-integrity procs; Scour only where it alone
/// finishes. For a delivery the target and the floor are both the top band — GatherBuddy's
/// comment reads "for custom deliveries we always want max collectability", and that is how it
/// implements it: the top threshold, with overshoot arriving on its own from boosted raises.
/// </para>
/// </summary>
public static class CollectableRotation
{
    /// <summary>Scrutiny's cost, from the Action sheet (22185 / 22189).</summary>
    public const int ScrutinyCost = 200;

    public static GatherMove Next(in CollectableState state)
    {
        // Cleared the bar: take it. Anything more is integrity spent for nothing.
        if (state.Collectability >= state.Target)
            return GatherMove.Collect;

        // The last point: bank it if it is worth banking. Below the floor a Collect is junk, and
        // Meticulous's chance of leaving the point untouched is the only route to anything.
        if (state.IntegrityLeft <= 1)
            return state.Collectability >= state.Minimum || state.MeticulousYield <= 0
                ? GatherMove.Collect
                : GatherMove.Meticulous;

        // Without the window's numbers there is nothing to reason with, so take the reliable one.
        if (state.ScourYield <= 0)
            return GatherMove.Scour;

        // A swing that finishes now: Meticulous where it reaches — it may save the point as well —
        // and Scour only where it alone finishes.
        if (state.MeticulousYield > 0 && state.Collectability + state.MeticulousYield >= state.Target)
            return GatherMove.Meticulous;
        if (state.Collectability + state.ScourYield >= state.Target)
            return GatherMove.Scour;

        // Nothing finishes from here: Scrutiny while the GP lasts, then Meticulous as the raise —
        // its free-integrity procs are what stretch a node, and integrity is items.
        if (!state.ScrutinyUsed && state.Gp - ScrutinyCost >= state.GpReserve)
            return GatherMove.Scrutiny;

        return state.MeticulousYield > 0 ? GatherMove.Meticulous : GatherMove.Scour;
    }

    /// <summary>The action ids for a move, per class. Read off the Action sheet 2026-08-21.</summary>
    public static uint ActionId(GatherMove move, uint classJobId)
    {
        var miner = classJobId == 16;
        return move switch
        {
            GatherMove.Collect => miner ? 240u : 815u,
            GatherMove.Scour => miner ? 22182u : 22186u,
            GatherMove.Brazen => miner ? 22183u : 22187u,
            GatherMove.Meticulous => miner ? 22184u : 22188u,
            GatherMove.Scrutiny => miner ? 22185u : 22189u,
            _ => 0u,
        };
    }
}
