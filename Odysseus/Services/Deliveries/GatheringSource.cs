using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>Where a gathered or fished item comes from.</summary>
/// <param name="Job">Miner, Botanist or Fisher — whichever class works this node.</param>
/// <param name="Level">The node's gathering level.</param>
/// <param name="Place">The named spot, e.g. "Yak T'el"; empty when the sheets do not say.</param>
public sealed record GatheringOrigin(string Job, int Level, string Place, string Zone)
{
    public string Describe()
    {
        var where = (Place.Length, Zone.Length) switch
        {
            (> 0, > 0) when !Place.Equals(Zone, StringComparison.OrdinalIgnoreCase) => $"{Place}, {Zone}",
            (> 0, _) => Place,
            (_, > 0) => Zone,
            _ => "an unlisted node",
        };
        return $"{where} — {Job} Lv {Level}";
    }
}

/// <summary>Looks up where a gathered item is found.</summary>
public interface IGatheringSource
{
    /// <summary>Null when nothing in the sheets gathers this item.</summary>
    GatheringOrigin? For(uint itemId);
}

/// <summary>
/// Gathering nodes, read from <c>GatheringItem</c> → <c>GatheringPointBase</c> → <c>GatheringPoint</c>.
///
/// <para>
/// This tells you where to go; it does not go there. Odysseus has no gathering of its own yet, so a
/// gather or fish delivery with an empty bag stops and names the item, the collectability, the job
/// and the place — which is the honest version of "not built" and still saves the lookup.
/// </para>
///
/// <para>
/// Node coordinates are deliberately not used. <c>ExportedGatheringPoint</c> gives an X and a Z but
/// no height, and walking to a guessed height is how you end up swimming under a cliff. Automating
/// the node itself needs that solved first.
/// </para>
/// </summary>
public sealed class GatheringSource : IGatheringSource
{
    /// <summary>GatheringType 0–1 are Miner's, 2–3 Botanist's, 4–5 Fisher's.</summary>
    private static string JobOf(uint gatheringType) => gatheringType switch
    {
        0 or 1 => "Miner",
        2 or 3 => "Botanist",
        _ => "Fisher",
    };

    private readonly IDataManager _data;
    private readonly Action<string>? _log;
    private readonly Dictionary<uint, GatheringOrigin?> _cache = new();

    public GatheringSource(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public GatheringOrigin? For(uint itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached)) return cached;

        GatheringOrigin? origin = null;
        try
        {
            // Which GatheringItem row is this item?
            var gatheringItem = _data.GetExcelSheet<GatheringItem>()
                .FirstOrDefault(g => g.Item.RowId == itemId);
            if (gatheringItem.RowId != 0 || gatheringItem.Item.RowId == itemId)
                origin = FromGatheringItem(gatheringItem.RowId);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Gathering lookup for item {itemId} failed: {ex.Message}");
        }

        _cache[itemId] = origin;
        return origin;
    }

    private GatheringOrigin? FromGatheringItem(uint gatheringItemId)
    {
        foreach (var b in _data.GetExcelSheet<GatheringPointBase>())
        {
            var found = false;
            foreach (var slot in b.Item)
            {
                if (slot.RowId != gatheringItemId) continue;
                found = true;
                break;
            }
            if (!found) continue;

            // Any point on this base will do — they are the same node in different instances.
            var point = _data.GetExcelSheet<GatheringPoint>()
                .FirstOrDefault(p => p.GatheringPointBase.RowId == b.RowId);
            var place = point.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;
            var zone = point.TerritoryType.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty;

            // Whether the node is timed is not said here — that lives in GatheringPointTransient —
            // and it is not worth chasing while nothing walks to the node anyway.
            return new GatheringOrigin(JobOf(b.GatheringType.RowId), b.GatheringLevel, place, zone);
        }
        return null;
    }
}
