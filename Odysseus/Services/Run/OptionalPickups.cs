using System;
using System.Collections.Generic;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Run;

/// <summary>
/// The side quests a path picks up on the way, and when a run leaves them.
///
/// <para>
/// Questionable's authors fold side-quest pick-ups into story paths — The Key to Victory (2549)
/// crosses into The Peaks to take A Hunger for Trade and Closing Up Shop before its solo duty, then
/// teleports back. 146 pick-up steps across 106 quests, 134 of them inside MSQ paths. Measured
/// against the quest sheet's own <c>PreviousQuest</c> links on 2026-09-26, <b>none</b> of the 146 is
/// a prerequisite of any MSQ quest: they are conveniences for a later side chain, never the story's.
/// </para>
///
/// <para>
/// A pick-up is left when its quest is already taken (always — there is nothing to pick up), or
/// when the setting says to skip optional ones and nothing the run is working towards needs it.
/// The chain check stays although today it never fires: a future bundle adding a pick-up the story
/// does need must not be able to strand a run.
/// </para>
///
/// <para>
/// The walk <i>to</i> a pick-up is the other half. Skipping the pick-ups while still crossing a zone
/// to stand where they would have been is the wasted movement this exists to remove — so a walk
/// whose only destination was pick-ups that will not happen is left too.
/// </para>
/// </summary>
public static class OptionalPickups
{
    /// <summary>The pick-up's quest is already in the journal or done.</summary>
    public static bool Taken(QuestStep step, IConditionWorld world)
        => step.PickUpQuestId is { } quest && (world.IsQuestAccepted(quest) || world.IsQuestComplete(quest));

    /// <summary>This run will not do the pick-up.</summary>
    /// <param name="skipOptional">The setting: leave pick-ups nothing needs.</param>
    /// <param name="needed">Whether a quest is on the chain the run is working towards.</param>
    public static bool Dropped(QuestStep step, IConditionWorld world, bool skipOptional, Func<ushort, bool> needed)
        => step.PickUpQuestId is { } quest
           && (Taken(step, world) || (skipOptional && !needed(quest)));

    /// <summary>
    /// A walk that leads only to dropped pick-ups: the steps right after it are all dropped, and the
    /// first step that will actually run is in a different zone from where the walk ends (or there
    /// is none). A walk followed by real work in the same zone is kept — it is still the way there.
    /// </summary>
    public static bool LeadsOnlyToDropped(IReadOnlyList<QuestStep> steps, int index, IConditionWorld world,
        bool skipOptional, Func<ushort, bool> needed)
    {
        var walk = steps[index];
        if (walk.Kind != StepKind.WalkTo)
            return false;

        var next = index + 1;
        while (next < steps.Count && Dropped(steps[next], world, skipOptional, needed))
            next++;
        if (next == index + 1)
            return false; // nothing after it is being left: this is an ordinary walk

        var endsIn = walk.TargetTerritoryId ?? walk.TerritoryId;
        return next >= steps.Count || steps[next].TerritoryId != endsIn;
    }
}
