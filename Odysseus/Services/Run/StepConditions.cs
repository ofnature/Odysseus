using System.Collections.Generic;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;

namespace Odysseus.Services.Run;

/// <summary>What a condition can ask about. Kept tiny so it can be faked in tests.</summary>
public interface IConditionWorld
{
    uint TerritoryId { get; }
    bool CanFlyHere { get; }

    /// <summary>A zone from the base game — where the path data's flying is not to be trusted.</summary>
    bool InBaseGameZone { get; }
    bool IsQuestComplete(ushort questId);
    bool IsQuestAccepted(ushort questId);

    /// <summary>How many of an item are held, both qualities — HQ counts, the game accepts it.</summary>
    int ItemCount(uint itemId);
}

/// <summary>
/// The same world with flight declared off.
///
/// <para>
/// An allied society path in a base-game zone is run on the ground, and the path data is written in
/// terms the game uses: waypoints authored for a flight carry
/// <c>SkipConditions.StepIf.Flying = Locked</c>. Answering "locked" here is what drops them, so the
/// ground route the author wrote underneath is the one that runs — telling the executor not to fly
/// while still claiming flight is unlocked would leave it walking to waypoints in mid-air.
/// </para>
/// </summary>
public sealed class GroundedWorld(IConditionWorld inner) : IConditionWorld
{
    public uint TerritoryId => inner.TerritoryId;
    public bool CanFlyHere => false;
    public bool InBaseGameZone => inner.InBaseGameZone;
    public bool IsQuestComplete(ushort questId) => inner.IsQuestComplete(questId);
    public bool IsQuestAccepted(ushort questId) => inner.IsQuestAccepted(questId);
    public int ItemCount(uint itemId) => inner.ItemCount(itemId);
}

/// <summary>Evaluates <see cref="StepCondition"/> against live state. Pure; the only inputs are the interface and the snapshot.</summary>
public static class StepConditions
{
    /// <summary>True when every specified clause holds. An empty or null condition is <i>false</i> — "skip if nothing" must never skip.</summary>
    public static bool Holds(StepCondition? condition, IConditionWorld world, QuestSnapshot quest, QuestStep? step = null)
    {
        if (condition is null || condition.IsEmpty)
            return false;

        if (condition.InTerritory is { } inTerr && !inTerr.Contains(world.TerritoryId))
            return false;
        if (condition.NotInTerritory is { } notIn && notIn.Contains(world.TerritoryId))
            return false;
        if (condition.QuestsCompleted is { } completed && !All(completed, world.IsQuestComplete))
            return false;
        if (condition.QuestsAccepted is { } accepted && !All(accepted, world.IsQuestAccepted))
            return false;
        if (condition.Flying is { } flying)
        {
            var wantUnlocked = flying.Equals("Unlocked", System.StringComparison.OrdinalIgnoreCase);
            if (world.CanFlyHere != wantUnlocked)
                return false;
        }
        if (condition.CompletionQuestVariablesFlags is { } flags && !quest.Satisfies(flags))
            return false;

        if (condition.Item is { } item)
        {
            // The clause is about the step's own item, so without a step there is nothing to ask.
            if (step?.ItemId is not { } itemId) return false;
            var held = world.ItemCount(itemId) >= (step.ItemCount ?? 1);
            if (held == item.NotInInventory) return false;
        }

        // AetheryteUnlocked is still not evaluated; it is only ever seen on teleport clauses, where
        // a wrong answer costs a walk rather than the quest.
        return true;
    }

    /// <summary>The step itself should be skipped right now.</summary>
    public static bool ShouldSkipStep(QuestStep step, IConditionWorld world, QuestSnapshot quest)
        => Holds(step.SkipConditions?.StepIf, world, quest, step);

    /// <summary>The step's aetheryte teleport should be skipped (already nearby, etc.).</summary>
    public static bool ShouldSkipAetheryte(QuestStep step, IConditionWorld world, QuestSnapshot quest)
        => Holds(step.SkipConditions?.AetheryteShortcutIf, world, quest);

    private static bool All(List<ushort> ids, System.Func<ushort, bool> test)
    {
        foreach (var id in ids)
            if (!test(id))
                return false;
        return true;
    }
}
