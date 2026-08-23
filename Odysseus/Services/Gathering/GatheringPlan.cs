using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Odysseus.Services.Deliveries;

namespace Odysseus.Services.Gathering;

/// <summary>Where to go, as what, to gather one item.</summary>
/// <param name="Spawns">Every spot the node appears at, in the order they should be tried.</param>
public sealed record GatheringTarget(
    uint ItemId, uint NodeId, uint TerritoryId, uint ClassJobId, ushort Level, IReadOnlyList<Vector3> Spawns);

/// <summary>
/// Turns "I need this item" into "stand here, as this class".
///
/// <para>
/// Pure, so the choice can be pinned by tests: the sheets say which points yield the item and where
/// they are, the atlas says the spots each one spawns at, and this picks between them. Preference is
/// the node with the most spawn points among those we can actually reach — more spots means longer
/// before the run has to walk somewhere else — and a point the sheets cannot place is no use however
/// many spots it has.
/// </para>
/// </summary>
public static class GatheringPlan
{
    public static GatheringTarget? For(uint itemId, IGatheringSource source, NodeAtlas atlas)
        => All(itemId, source, atlas).FirstOrDefault();

    /// <summary>Every workable node for the item, best first. More than one matters when the first is unreachable.</summary>
    public static IReadOnlyList<GatheringTarget> All(uint itemId, IGatheringSource source, NodeAtlas atlas)
    {
        var targets = new List<GatheringTarget>();
        foreach (var point in source.PointsFor(itemId))
        {
            if (!point.HasZone)
                continue; // the sheet leaves a placeholder behind; there is nowhere to send anyone
            var spawns = atlas.SpawnsOf(point.NodeId);
            if (spawns.Count == 0)
                continue;
            targets.Add(new GatheringTarget(itemId, point.NodeId, point.TerritoryId, point.ClassJobId, point.Level, spawns));
        }

        return targets
            .OrderByDescending(t => t.Spawns.Count)
            .ThenBy(t => t.NodeId)
            .ToList();
    }
}
