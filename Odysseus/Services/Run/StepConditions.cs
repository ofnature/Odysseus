using System.Collections.Generic;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;

namespace Odysseus.Services.Run;

/// <summary>What a condition can ask about. Kept tiny so it can be faked in tests.</summary>
public interface IConditionWorld
{
    uint TerritoryId { get; }
    bool CanFlyHere { get; }
    bool IsQuestComplete(ushort questId);
    bool IsQuestAccepted(ushort questId);
}

/// <summary>Evaluates <see cref="StepCondition"/> against live state. Pure; the only inputs are the interface and the snapshot.</summary>
public static class StepConditions
{
    /// <summary>True when every specified clause holds. An empty or null condition is <i>false</i> — "skip if nothing" must never skip.</summary>
    public static bool Holds(StepCondition? condition, IConditionWorld world, QuestSnapshot quest)
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

        // AetheryteUnlocked / NotInInventory are not evaluated yet — treated as "holds", which is
        // the conservative direction for a skip (skipping a teleport we could have taken costs a
        // walk; skipping a step we needed costs the quest). Revisit when a path needs them.
        return true;
    }

    /// <summary>The step itself should be skipped right now.</summary>
    public static bool ShouldSkipStep(QuestStep step, IConditionWorld world, QuestSnapshot quest)
        => Holds(step.SkipConditions?.StepIf, world, quest);

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
