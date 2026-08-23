using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Gathering;

/// <summary>Where to fish for one item.</summary>
/// <param name="Centre">World coordinates, with no height — the sheets do not record one.</param>
/// <param name="Radius">The spot's own radius, in the same map units the centre came from.</param>
public sealed record FishingSpotRef(
    uint ItemId, uint SpotId, uint TerritoryId, Vector3 Centre, int Radius, bool IsSpearFish, string Place);

/// <summary>Looks up where a fish is caught.</summary>
public interface IFishingCatalog
{
    /// <summary>Null when nothing in the sheets fishes this item.</summary>
    FishingSpotRef? For(uint itemId);
}

/// <summary>
/// Fishing and spearfishing spots, from the game's own sheets.
///
/// <para>
/// For a custom delivery the spot does not have to be searched for: <c>SatisfactionSupply</c> names
/// <c>FishingSpotId</c> or <c>SpearFishingSpotId</c> against the request itself, which is the game
/// telling us exactly where it means. Anything else falls back to searching <c>FishingSpot</c> and
/// <c>SpearfishingItem</c> for the item, which is what a delivery-agnostic caller needs.
/// </para>
///
/// <para>
/// Both are recorded in map pixels; <see cref="MapCoordinates"/> converts them. Neither records a
/// height, so the result is somewhere to stand near rather than on.
/// </para>
/// </summary>
public sealed class FishingCatalog : IFishingCatalog
{
    private readonly IDataManager _data;
    private readonly Action<string>? _log;
    private readonly Dictionary<uint, FishingSpotRef?> _cache = new();

    public FishingCatalog(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public FishingSpotRef? For(uint itemId)
    {
        if (_cache.TryGetValue(itemId, out var cached)) return cached;

        FishingSpotRef? found = null;
        try
        {
            found = FromDeliverySheet(itemId) ?? FromFishingSpot(itemId) ?? FromSpearfishing(itemId);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Fishing lookup for item {itemId} failed: {ex.Message}");
        }

        _cache[itemId] = found;
        return found;
    }

    /// <summary>The delivery sheet names the spot outright, which beats searching for it.</summary>
    private FishingSpotRef? FromDeliverySheet(uint itemId)
    {
        foreach (var row in _data.GetSubrowExcelSheet<SatisfactionSupply>())
            foreach (var r in row)
            {
                if (r.Item.RowId != itemId) continue;
                if (r.FishingSpotId != 0) return Spot(itemId, r.FishingSpotId);
                if (r.SpearFishingSpotId != 0) return Hole(itemId, r.SpearFishingSpotId);
            }
        return null;
    }

    private FishingSpotRef? FromFishingSpot(uint itemId)
    {
        foreach (var spot in _data.GetExcelSheet<FishingSpot>())
            if (spot.Item.Any(i => i.RowId == itemId))
                return Spot(itemId, spot.RowId);
        return null;
    }

    private FishingSpotRef? FromSpearfishing(uint itemId)
    {
        foreach (var item in _data.GetExcelSheet<SpearfishingItem>())
            if (item.Item.RowId == itemId && item.TerritoryType.RowId != 0)
                return Hole(itemId, item.TerritoryType.RowId);
        return null;
    }

    private FishingSpotRef? Spot(uint itemId, uint spotId)
    {
        if (_data.GetExcelSheet<FishingSpot>().GetRowOrDefault(spotId) is not { } spot)
            return null;
        var territory = spot.TerritoryType.ValueNullable;
        return new FishingSpotRef(itemId, spotId, spot.TerritoryType.RowId,
            World(spot.X, spot.Z, territory?.Map.ValueNullable), spot.Radius, false,
            spot.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty);
    }

    private FishingSpotRef? Hole(uint itemId, uint notebookId)
    {
        if (_data.GetExcelSheet<SpearfishingNotebook>().GetRowOrDefault(notebookId) is not { } book)
            return null;
        var territory = book.TerritoryType.ValueNullable;
        return new FishingSpotRef(itemId, notebookId, book.TerritoryType.RowId,
            World(book.X, book.Y, territory?.Map.ValueNullable), book.Radius, true,
            book.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty);
    }

    private static Vector3 World(int x, int z, Map? map) => new(
        MapCoordinates.ToWorld(x, map?.SizeFactor ?? 100, map?.OffsetX ?? 0),
        0f,
        MapCoordinates.ToWorld(z, map?.SizeFactor ?? 100, map?.OffsetY ?? 0));
}
