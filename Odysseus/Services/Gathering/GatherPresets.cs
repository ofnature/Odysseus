using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Gathering;

/// <summary>A ready-made list: a name and the items it starts with.</summary>
public sealed record GatherPreset(string Name, IReadOnlyList<uint> ItemIds);

/// <summary>
/// The lists worth having before anyone types anything.
///
/// <para>
/// Elemental crystals are the case that asks for this: eighteen items, wanted in the same three
/// groups by everyone, and adding them one at a time through a search box is exactly the chore a
/// preset removes. Their ids are one clean block in the Item sheet — shards 2–7, crystals 8–13,
/// clusters 14–19, each in the order Fire, Ice, Wind, Earth, Lightning, Water — and all eighteen
/// have GatheringItem rows, so every one of them can actually be gathered (checked against the
/// sheets 2026-09-20).
/// </para>
///
/// <para>
/// Hardcoded on purpose. These are facts about the game, not preferences: a preset is a starting
/// point the user edits afterwards, and the list it makes is an ordinary list with nothing
/// special about it.
/// </para>
/// </summary>
public static class GatherPresets
{
    private static readonly uint[] Shards = [2, 3, 4, 5, 6, 7];
    private static readonly uint[] Crystals = [8, 9, 10, 11, 12, 13];
    private static readonly uint[] Clusters = [14, 15, 16, 17, 18, 19];

    public static readonly IReadOnlyList<GatherPreset> All =
    [
        new("Shards", Shards),
        new("Crystals", Crystals),
        new("Clusters", Clusters),
        new("Shards, crystals and clusters", [.. Shards, .. Crystals, .. Clusters]),
    ];

    /// <summary>A new list from the preset. Targets are the same default as adding an item by hand.</summary>
    public static GatherList Build(GatherPreset preset) => new()
    {
        Name = preset.Name,
        Items = preset.ItemIds.Select(id => new GatherListItem { ItemId = id }).ToList(),
    };
}
