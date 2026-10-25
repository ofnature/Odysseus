using System.Collections.Generic;

namespace Odysseus.Services.Gathering;

/// <summary>One line of a gather list: an item and the bag count to gather it up to.</summary>
public sealed class GatherListItem
{
    public uint ItemId { get; set; }

    /// <summary>The target is a bag count, as GatherBuddy's is: done when the bag holds this many.</summary>
    public int TargetCount { get; set; } = 99;
}

/// <summary>
/// Legacy: the gather lists moved to Demeter on 2026-10-22, which brings a saved list over from
/// Odysseus's config once. This type stays only so Odysseus does not drop the saved lists before
/// Demeter has read them; remove it with <see cref="Config.OdysseusConfig.GatherLists"/>.
///
/// A named list of things to gather. Several can exist; a run takes every enabled one and
/// gathers whatever is short, grouped by zone so a three-zone list costs three teleports.
/// </summary>
public sealed class GatherList
{
    public string Name { get; set; } = "New list";
    public bool Enabled { get; set; } = true;

    /// <summary>Drop items that have reached their target when a run ends.</summary>
    public bool RemoveCompleted { get; set; }

    public List<GatherListItem> Items { get; set; } = [];
}
