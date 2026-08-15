using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Travel;

/// <summary>
/// Resolves the path data's aetheryte names to aetheryte ids, from the game's own sheet.
///
/// <para>
/// The bundle spells aetherytes the way a player would, not the way the sheet does:
/// <c>"Lochs - Ala Mhigan Quarter"</c> for <i>The Lochs</i> / <i>The Ala Mhigan Quarter</i>,
/// <c>"Ishgard"</c> for <i>Foundation</i>, and — inconsistently — <c>"The Churning Mists - Moghome"</c>
/// with the article kept. Measured 2026-08-15 across all 107 distinct names in the bundle: canonicalising
/// <b>both</b> sides the same way (strip a leading "The " from each " - " half, "Rak'tika Greatwood"
/// → "Rak'tika") plus the eight city aliases below resolves every one. That measurement is the
/// spec; if a future bundle adds a name this cannot resolve, the executor fails the step and says
/// which name.
/// </para>
/// </summary>
public sealed class AetheryteCatalog
{
    /// <summary>Colloquial city names the data uses → the aetheryte's PlaceName in the sheet.</summary>
    private static readonly Dictionary<string, string> CityAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ishgard"] = "Foundation",
        ["Crystarium"] = "The Crystarium",
        ["Gridania"] = "New Gridania",
        ["Limsa Lominsa"] = "Limsa Lominsa Lower Decks",
        ["Ul'dah"] = "Ul'dah - Steps of Nald",
        ["Mor Dhona"] = "Revenant's Toll",
        ["Doman Enclave"] = "The Doman Enclave",
        ["Gold Saucer"] = "The Gold Saucer",
    };

    private readonly Dictionary<string, uint> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, (string Name, uint TerritoryId)> _byId = new();

    /// <summary>Build from the game's sheets.</summary>
    public AetheryteCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            var rows = data.GetExcelSheet<Aetheryte>()
                .Where(a => a.IsAetheryte)
                .Select(a => (
                    Id: a.RowId,
                    Name: a.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    Zone: a.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    Territory: a.Territory.RowId));
            Load(rows);
        }
        catch (Exception ex)
        {
            log($"Aetheryte catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Build from explicit rows — what the tests use.</summary>
    public AetheryteCatalog(IEnumerable<(uint Id, string Name, string Zone, uint Territory)> rows) => Load(rows);

    private void Load(IEnumerable<(uint Id, string Name, string Zone, uint Territory)> rows)
    {
        foreach (var (id, name, zone, territory) in rows)
        {
            if (name.Length == 0)
                continue;
            _byId[id] = (name, territory);
            // Sheet-exact and canonical forms both, so either spelling in the data hits.
            _byName.TryAdd(name, id);
            _byName.TryAdd(Canon(name), id);
            _byName.TryAdd($"{Canon(zone)} - {Canon(name)}", id);
        }
        foreach (var (alias, real) in CityAliases)
            if (_byName.TryGetValue(real, out var id))
                _byName[alias] = id;
    }

    public int Count => _byId.Count;

    /// <summary>Aetheryte id for a name as the path data spells it, or null when it cannot be resolved.</summary>
    public uint? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (_byName.TryGetValue(name.Trim(), out var id))
            return id;
        var canon = string.Join(" - ", name.Split(" - ").Select(Canon));
        return _byName.TryGetValue(canon, out id) ? id : null;
    }

    public uint? TerritoryOf(uint aetheryteId)
        => _byId.TryGetValue(aetheryteId, out var v) ? v.TerritoryId : null;

    public string NameOf(uint aetheryteId)
        => _byId.TryGetValue(aetheryteId, out var v) ? v.Name : $"aetheryte {aetheryteId}";

    /// <summary>The one canonical spelling both sides are reduced to.</summary>
    public static string Canon(string part)
    {
        var s = part.Trim();
        if (s.StartsWith("The ", StringComparison.OrdinalIgnoreCase))
            s = s[4..];
        if (s.Equals("Rak'tika Greatwood", StringComparison.OrdinalIgnoreCase))
            s = "Rak'tika";
        return s;
    }
}
