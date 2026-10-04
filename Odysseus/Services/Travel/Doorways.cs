using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Travel;

/// <summary>
/// The doors into zones with no aetheryte of their own — the Rising Stones from Mor Dhona, inn
/// rooms, Grand Company offices. No sheet lists them; the path library does: every step that
/// interacts with an event object and ends in another territory is a door, and there are over
/// three hundred of them. Built once, from whatever library is loaded.
/// </summary>
public sealed class Doorways
{
    public sealed record Door(uint From, uint Into, uint DataId, Vector3 At);

    /// <summary>
    /// Ways in the library cannot see, because the step that takes them names no zone change.
    /// The Crystarium's aspiring amaro tamer flies you to Kholusia in In Search of Alphinaud (3282)
    /// and keeps asking "Travel to Kholusia?" after — the only way in before Kholusia's aetherytes
    /// are attuned (A Still Tide, 3283, starts there).
    /// </summary>
    private static readonly Door[] Rides =
    [
        new(819, 814, 1029806, new Vector3(62.39f, 36.25f, -169.39f)),
    ];

    private readonly Func<IEnumerable<QuestPath>> _paths;
    private Dictionary<uint, List<Door>>? _into;
    private Dictionary<uint, List<Door>>? _gates;

    /// <summary>
    /// Gates inside a zone: an interact that moves you across a wall without leaving it — the Peaks'
    /// Ala Mhigan Resistance gate guards. The path data writes them as an interact whose target
    /// territory is the one it stands in. Taken when a walk finds no way across on the mesh.
    /// </summary>
    public IReadOnlyList<Door> GatesIn(uint territoryId)
    {
        _gates ??= _paths().SelectMany(p => p.Sequences.SelectMany(s => s.Steps))
            .Where(st => st.Kind == StepKind.Interact && st.DataId is not null && st.Position is not null
                         && st.TerritoryId != 0 && st.TargetTerritoryId == st.TerritoryId)
            .GroupBy(st => (st.TerritoryId, DataId: st.DataId!.Value))
            .Select(g => new Door(g.Key.TerritoryId, g.Key.TerritoryId, g.Key.DataId, g.First().Position!.Value))
            .GroupBy(d => d.From)
            .ToDictionary(g => g.Key, g => g.ToList());
        return _gates.TryGetValue(territoryId, out var gates) ? gates : [];
    }

    public Doorways(Func<IEnumerable<QuestPath>> paths) => _paths = paths;

    /// <summary>
    /// Every door into this territory, most used first. Which to take is the caller's: the one
    /// standing here, else one in a zone it can reach — the most used into Kholusia stands in a zone
    /// a new character cannot reach, and picking it blindly stranded newtoon1 in the Ocular.
    /// </summary>
    public IReadOnlyList<Door> Into(uint territoryId)
    {
        _into ??= Index(_paths());
        return _into.TryGetValue(territoryId, out var doors) ? doors : [];
    }

    /// <summary>Doors by the territory they lead into, most used first.</summary>
    public static Dictionary<uint, List<Door>> Index(IEnumerable<QuestPath> paths) =>
        paths.SelectMany(p => p.Sequences.SelectMany(s => s.Steps))
            .Where(st => st.Kind == StepKind.Interact && st.DataId >= 2000000 && st.Position is not null
                         && st.TerritoryId != 0 && st.TargetTerritoryId is { } into && into != st.TerritoryId)
            .GroupBy(st => (st.TerritoryId, Into: st.TargetTerritoryId!.Value, DataId: st.DataId!.Value))
            .OrderByDescending(g => g.Count())
            .Select(g => new Door(g.Key.TerritoryId, g.Key.Into, g.Key.DataId, g.First().Position!.Value))
            .Concat(Rides)
            .GroupBy(d => d.Into)
            .ToDictionary(g => g.Key, g => g.ToList());
}
