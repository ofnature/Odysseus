using System;
using System.Collections.Generic;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Run;

/// <summary>
/// Whether a <c>PurchaseItem</c> step still has a purpose by the time the run reaches it.
///
/// <para>
/// The path data is step-local: a sequence buys three Copper Ore, crafts one Copper Ingot from
/// them, and hands the ingot in — but each step's "skip if already held" clause asks only about
/// its <i>own</i> item. Arrive holding the ingot and the craft correctly skips while the purchase
/// still runs, buying materials for something that will never be made.
/// </para>
///
/// <para>
/// So the question asked here is the one the data does not: is anything left in this sequence that
/// will actually consume what this step is about to buy? A purchase is kept unless every later
/// craft it feeds is already satisfied — and kept outright if anything else in the sequence names
/// the item, because then it is wanted for its own sake rather than as an ingredient.
/// </para>
/// </summary>
public static class PurchasePlan
{
    /// <summary>What crafting one of an item consumes. Empty for anything with no recipe.</summary>
    public delegate IReadOnlyList<uint> IngredientsOf(uint itemId);

    /// <summary>
    /// True when the purchase is still worth making.
    /// </summary>
    /// <param name="steps">The sequence the step belongs to.</param>
    /// <param name="index">Which step is about to run.</param>
    /// <param name="held">How many of an item are in the bags.</param>
    /// <param name="ingredientsOf">Item → the items its recipe consumes.</param>
    public static bool IsWorthBuying(
        IReadOnlyList<QuestStep> steps, int index, Func<uint, int> held, IngredientsOf ingredientsOf)
    {
        if (index < 0 || index >= steps.Count)
            return true;
        var step = steps[index];
        if (step.Kind != StepKind.PurchaseItem || step.ItemId is not { } buying)
            return true;

        var feedsSomething = false;
        var everyCraftSatisfied = true;

        for (var i = index + 1; i < steps.Count; i++)
        {
            var later = steps[i];

            if (later.Kind == StepKind.Craft && later.ItemId is { } making)
            {
                var consumes = false;
                foreach (var ingredient in ingredientsOf(making))
                    if (ingredient == buying) { consumes = true; break; }
                if (!consumes)
                    continue;

                feedsSomething = true;
                if (held(making) < Math.Max(1, later.ItemCount ?? 1))
                    everyCraftSatisfied = false;
                continue;
            }

            // Wanted by name for its own sake — used, equipped, gathered up to a count, bought
            // again. Nothing about a satisfied craft says anything about that.
            if (later.ItemId == buying)
                return true;
            if (later.GatherItems is { } targets)
                foreach (var target in targets)
                    if (target.ItemId == buying)
                        return true;
        }

        return !feedsSomething || !everyCraftSatisfied;
    }

    /// <summary>
    /// The same judgement for a Gather step: skip it only when every target it is still short of
    /// exists solely to feed crafts that are already made. One Size Fits All's happi sets stood
    /// crafted 3/3 while the gather step demanded the malachite they had consumed — the bundle
    /// orders the craft before its gathers, which is also why this scans the whole block.
    /// </summary>
    public static bool IsWorthGathering(
        IReadOnlyList<QuestStep> steps, int index, Func<uint, int> held, IngredientsOf ingredientsOf)
    {
        if (index < 0 || index >= steps.Count)
            return true;
        var step = steps[index];
        if (step.Kind != StepKind.Gather || step.GatherItems is not { Count: > 0 } targets)
            return true;

        foreach (var target in targets)
        {
            if (held(target.ItemId) >= target.ItemCount)
                continue; // covered either way; says nothing about the rest
            if (WorthAcquiring(steps, index, target.ItemId, held, ingredientsOf))
                return true;
        }
        return false;
    }

    /// <summary>Whole-block scan: the item is worth having unless it only feeds satisfied crafts.</summary>
    private static bool WorthAcquiring(
        IReadOnlyList<QuestStep> steps, int index, uint item, Func<uint, int> held, IngredientsOf ingredientsOf)
    {
        var feedsSomething = false;
        var everyCraftSatisfied = true;

        for (var i = 0; i < steps.Count; i++)
        {
            if (i == index)
                continue;
            var other = steps[i];

            if (other.Kind == StepKind.Craft && other.ItemId is { } making)
            {
                var consumes = false;
                foreach (var ingredient in ingredientsOf(making))
                    if (ingredient == item) { consumes = true; break; }
                if (!consumes)
                    continue;

                feedsSomething = true;
                if (held(making) < Math.Max(1, other.ItemCount ?? 1))
                    everyCraftSatisfied = false;
                continue;
            }

            // Wanted by name for its own sake — used, equipped, gathered up to a count, bought
            // again. Nothing about a satisfied craft says anything about that.
            if (other.ItemId == item)
                return true;
            if (other.GatherItems is { } wanted)
                foreach (var t in wanted)
                    if (t.ItemId == item)
                        return true;
        }

        return !feedsSomething || !everyCraftSatisfied;
    }
}
