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

/// <summary>One gathering point: what to walk to, in which zone, as which class.</summary>
/// <param name="NodeId">The <c>GatheringPoint</c> row — also the object's data id.</param>
/// <param name="TerritoryId">Zero or one both mean the sheet does not say.</param>
/// <param name="GatheringType">0–1 Miner, 2–3 Botanist, 4–5 Fisher.</param>
public sealed record GatheringPointRef(uint NodeId, uint TerritoryId, uint GatheringType, ushort Level)
{
    /// <summary>The sheet leaves a placeholder row behind for points it does not place.</summary>
    public bool HasZone => TerritoryId > 1;

    public uint ClassJobId => GatheringType switch { 0 or 1 => 16u, 2 or 3 => 17u, _ => 18u };
}

/// <summary>Looks up where a gathered item is found.</summary>
public interface IGatheringSource
{
    /// <summary>Null when nothing in the sheets gathers this item.</summary>
    GatheringOrigin? For(uint itemId);

    /// <summary>
    /// Every <c>GatheringPointBase</c> that yields this item — usually two, a Miner's and a
    /// Botanist's. Empty when nothing gathers it. This is the key the imported node paths are
    /// stored under, so it is what turns "where is it" into "where do I stand".
    /// </summary>
    IReadOnlyList<uint> BasesFor(uint itemId);

    /// <summary>
    /// Every gathering point that yields this item, with the zone and class each needs. The node id
    /// doubles as the object's data id, which is how the atlas is keyed and how a node is
    /// interacted with.
    /// </summary>
    IReadOnlyList<GatheringPointRef> PointsFor(uint itemId);
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

    private readonly Dictionary<uint, IReadOnlyList<uint>> _basesCache = new();

    public IReadOnlyList<uint> BasesFor(uint itemId)
    {
        if (_basesCache.TryGetValue(itemId, out var cached)) return cached;

        var bases = new List<uint>();
        try
        {
            var gatheringItem = _data.GetExcelSheet<GatheringItem>()
                .FirstOrDefault(g => g.Item.RowId == itemId);
            if (gatheringItem.RowId != 0 || gatheringItem.Item.RowId == itemId)
                foreach (var b in _data.GetExcelSheet<GatheringPointBase>())
                    foreach (var slot in b.Item)
                        if (slot.RowId == gatheringItem.RowId)
                        {
                            bases.Add(b.RowId);
                            break;
                        }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Gathering points for item {itemId} could not be read: {ex.Message}");
        }

        _basesCache[itemId] = bases;
        return bases;
    }

    private readonly Dictionary<uint, IReadOnlyList<GatheringPointRef>> _pointsCache = new();

    public IReadOnlyList<GatheringPointRef> PointsFor(uint itemId)
    {
        if (_pointsCache.TryGetValue(itemId, out var cached)) return cached;

        var points = new List<GatheringPointRef>();
        try
        {
            var bases = BasesFor(itemId).ToHashSet();
            if (bases.Count > 0)
                foreach (var p in _data.GetExcelSheet<GatheringPoint>())
                {
                    if (!bases.Contains(p.GatheringPointBase.RowId)) continue;
                    var b = p.GatheringPointBase.ValueNullable;
                    points.Add(new GatheringPointRef(
                        p.RowId, p.TerritoryType.RowId, b?.GatheringType.RowId ?? 99, (ushort)(b?.GatheringLevel ?? 0)));
                }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Gathering points for item {itemId} could not be read: {ex.Message}");
        }

        _pointsCache[itemId] = points;
        return points;
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
