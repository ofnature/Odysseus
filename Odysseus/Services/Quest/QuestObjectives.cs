using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Odysseus.Services.Quest;

/// <summary>One place the game says an objective is.</summary>
public sealed record ObjectiveSpot(uint LevelId, Vector3 Position);

/// <summary>What the game's own to-do list says about one tracked quest.</summary>
public sealed record TrackedObjectives(
    ushort QuestId,
    string Label,
    IReadOnlyList<IReadOnlyList<ObjectiveSpot>> Objectives,
    IReadOnlyList<uint> ItemIds);

/// <summary>
/// The game's own answer to "where is the next objective".
///
/// <para>
/// Read-only, and deliberately so: this exists to be compared against the path a run is
/// following, to find out how much of a quest the game itself could drive. Every quest tracked in
/// the journal carries objectives, each objective a list of Level rows, and each Level row a
/// position in the world — which is the same question a <c>WalkTo</c> step answers, from data
/// nobody had to record.
/// </para>
///
/// <para>
/// What it does not carry is as important: no dialogue answers, no emotes or items to use, no
/// duty ids, and no ordering between two objectives that are both live. Those are why paths
/// exist. Nothing here is wired into a run.
/// </para>
/// </summary>
public sealed unsafe class QuestObjectives
{
    private readonly Action<string>? _log;

    public QuestObjectives(Action<string>? log = null) => _log = log;

    public IReadOnlyList<TrackedObjectives> Read()
    {
        var quests = new List<TrackedObjectives>();
        try
        {
            var state = UIState.Instance();
            if (state == null)
                return quests;

            ref var tracked = ref state->QuestTodoList.Todo.TrackedQuests;
            for (var i = 0; i < tracked.Count; i++)
            {
                var quest = tracked[i];
                var objectives = new List<IReadOnlyList<ObjectiveSpot>>();
                foreach (var objective in quest.Objectives)
                {
                    var spots = new List<ObjectiveSpot>();
                    foreach (var level in objective.LevelEntries)
                        spots.Add(new ObjectiveSpot(level.LevelId, new Vector3(level.X, level.Y, level.Z)));
                    objectives.Add(spots);
                }

                var items = new List<uint>();
                foreach (var item in quest.ItemIds)
                    if (item != 0)
                        items.Add(item);

                quests.Add(new TrackedObjectives(
                    (ushort)quest.QuestId, quest.Id.ToString(), objectives, items));
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Reading the quest to-do list failed: {ex.Message}");
        }
        return quests;
    }
}
