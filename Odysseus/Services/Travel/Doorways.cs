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

    private readonly Func<IEnumerable<QuestPath>> _paths;
    private Dictionary<uint, List<Door>>? _into;

    public Doorways(Func<IEnumerable<QuestPath>> paths) => _paths = paths;

    /// <summary>A door into this territory — one standing in <paramref name="here"/> first, else the most used.</summary>
    public Door? Into(uint territoryId, uint here)
    {
        _into ??= Index(_paths());
        if (!_into.TryGetValue(territoryId, out var doors))
            return null;
        return doors.FirstOrDefault(d => d.From == here) ?? doors[0];
    }

    /// <summary>Doors by the territory they lead into, most used first.</summary>
    public static Dictionary<uint, List<Door>> Index(IEnumerable<QuestPath> paths) =>
        paths.SelectMany(p => p.Sequences.SelectMany(s => s.Steps))
            .Where(st => st.Kind == StepKind.Interact && st.DataId >= 2000000 && st.Position is not null
                         && st.TerritoryId != 0 && st.TargetTerritoryId is { } into && into != st.TerritoryId)
            .GroupBy(st => (st.TerritoryId, Into: st.TargetTerritoryId!.Value, DataId: st.DataId!.Value))
            .OrderByDescending(g => g.Count())
            .Select(g => new Door(g.Key.TerritoryId, g.Key.Into, g.Key.DataId, g.First().Position!.Value))
            .GroupBy(d => d.Into)
            .ToDictionary(g => g.Key, g => g.ToList());
}
