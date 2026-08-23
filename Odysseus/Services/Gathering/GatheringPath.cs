using System.Collections.Generic;
using System.Numerics;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Gathering;

/// <summary>
/// Where to stand to work one gathering point, and how to get there.
///
/// <para>
/// Keyed by <c>GatheringPointBase</c>, which is what the game uses to say "this node yields these
/// items". A delivery item resolves to one or two bases — a Miner one and a Botanist one — and each
/// base is a set of node spawns spread over a small area, which is what <see cref="Groups"/> holds.
/// </para>
///
/// <para>
/// Converted from QuestFlow's <c>GatheringPaths</c>, whose travel steps are the same shape as a
/// quest step's, so they are parsed by the same code and run by the same executor.
/// </para>
/// </summary>
public sealed class GatheringPath
{
    /// <summary>Bumped when the stored shape changes; see <see cref="QuestPath.FormatVersion"/>.</summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>The <c>GatheringPointBase</c> row this works.</summary>
    public uint PointBaseId { get; set; }

    /// <summary>The place, as the source file names it — "Chabameki", "Yawtanane Grasslands".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>MIN or BTN, from the source file name. Fishing is not in this data at all.</summary>
    public string Job { get; set; } = string.Empty;

    /// <summary>Expansion and zone, from the folders — "7.x - Dawntrail/Urqopacha".</summary>
    public string Category { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    /// <summary>Where the file came from, so a re-import can tell an edit from an upstream change.</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>Getting to the area: teleports, aethernet hops, a walk. Ordinary steps.</summary>
    public List<QuestStep> Steps { get; set; } = [];

    /// <summary>Fly between the nodes rather than walking. Rare — five of the 175 source files.</summary>
    public bool FlyBetweenNodes { get; set; }

    public List<GatheringGroup> Groups { get; set; } = [];

    /// <summary>Every node location in the path, in order, flattened.</summary>
    public IEnumerable<GatheringLocation> AllLocations()
    {
        foreach (var group in Groups)
            foreach (var node in group.Nodes)
                foreach (var location in node.Locations)
                    yield return location;
    }
}

/// <summary>Nodes worked together before moving on — one rotation of a spawn cluster.</summary>
public sealed class GatheringGroup
{
    public List<GatheringNode> Nodes { get; set; } = [];
}

/// <summary>One node object, and the places it appears.</summary>
public sealed class GatheringNode
{
    /// <summary>The object's data id, the same currency the executor already interacts by.</summary>
    public uint DataId { get; set; }

    /// <summary>Fly to this one specifically. Two nodes in the whole source set say so.</summary>
    public bool Fly { get; set; }

    public List<GatheringLocation> Locations { get; set; } = [];
}

/// <summary>
/// One spawn of a node, and the arc to approach it from.
///
/// <para>
/// The angles are why this data is worth importing rather than walking to the object's own
/// position: a node on a cliff edge or inside a root system is only reachable from certain
/// directions, and the source has that measured for 983 of the 1,358 locations.
/// </para>
/// </summary>
public sealed class GatheringLocation
{
    public Vector3 Position { get; set; }

    public float? MinimumAngle { get; set; }
    public float? MaximumAngle { get; set; }
    public float? MinimumDistance { get; set; }
    public float? MaximumDistance { get; set; }
}
