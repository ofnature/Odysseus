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
    private readonly Dictionary<uint, (string Name, string Zone, uint TerritoryId, System.Numerics.Vector3? Position)> _byId = new();
    private readonly Dictionary<uint, string> _aliasById = new();

    /// <summary>
    /// One aethernet destination inside a city. <paramref name="Group"/> is the city's aethernet
    /// network — every shard and the city's own aetheryte share it, which is what says which
    /// aetheryte to teleport to before hopping.
    /// </summary>
    public sealed record Shard(uint Id, string Name, uint PlaceNameId, uint TerritoryId, byte Group, System.Numerics.Vector3? Position);

    private readonly List<Shard> _shards = [];
    /// <summary>Every aethernet stop by name — shards and city aetherytes alike.</summary>
    private readonly Dictionary<string, Shard> _stopsByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Aethernet group → the city aetheryte you can actually teleport to.</summary>
    private readonly Dictionary<byte, uint> _hubByGroup = new();
    /// <summary>Territory → its aethernet group, for "am I already in this city".</summary>
    private readonly Dictionary<uint, byte> _groupByTerritory = new();

    /// <summary>Build from the game's sheets.</summary>
    public AetheryteCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            var sheet = data.GetExcelSheet<Aetheryte>();
            var rows = sheet
                .Where(a => a.IsAetheryte)
                .Select(a => (
                    Id: a.RowId,
                    Name: a.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    Zone: a.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? string.Empty,
                    Territory: a.Territory.RowId,
                    Position: LevelPosition(a)));
            Load(rows);

            // The aethernet, kept apart from the teleport network because the two are reached by
            // different calls. Half a city can hold no aetheryte at all — Ul'dah's Steps of Thal
            // has six shards and none — so without this such a zone looks unreachable.
            foreach (var a in sheet)
            {
                if (a.RowId == 0) continue;
                var territory = a.Territory.RowId;
                if (territory == 0) continue;
                _groupByTerritory.TryAdd(territory, a.AethernetGroup);
                var name = a.AethernetName.ValueNullable?.Name.ExtractText() ?? string.Empty;
                // The PlaceName row is kept because that is what Lifestream's id-based hop takes —
                // an id cannot be mis-spelled, and both sides read it from the same sheet.
                var stop = new Shard(a.RowId, name, a.AethernetName.RowId, territory, a.AethernetGroup, LevelPosition(a));
                // A city aetheryte is an aethernet stop as well as a teleport target, so it is
                // named here too — "[Ul'dah] Aetheryte Plaza" is one, and every city has one.
                if (name.Length > 0) _stopsByName.TryAdd(name, stop);
                if (a.IsAetheryte)
                {
                    _hubByGroup.TryAdd(a.AethernetGroup, a.RowId);
                    continue;
                }
                if (name.Length == 0) continue;
                _shards.Add(stop);
            }
        }
        catch (Exception ex)
        {
            log($"Aetheryte catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static System.Numerics.Vector3? LevelPosition(Aetheryte a)
    {
        try
        {
            foreach (var l in a.Level)
                if (l.RowId != 0 && l.ValueNullable is { } lv)
                    return new System.Numerics.Vector3(lv.X, lv.Y, lv.Z);
        }
        catch
        {
            // A missing Level row is a missing position, not a missing aetheryte.
        }
        return null;
    }

    /// <summary>Build from explicit rows — what the tests use.</summary>
    public AetheryteCatalog(IEnumerable<(uint Id, string Name, string Zone, uint Territory)> rows)
        => Load(rows.Select(r => (r.Id, r.Name, r.Zone, r.Territory, (System.Numerics.Vector3?)null)));

    /// <summary>Build from explicit rows with positions.</summary>
    public AetheryteCatalog(IEnumerable<(uint Id, string Name, string Zone, uint Territory, System.Numerics.Vector3? Position)> rows) => Load(rows);

    private void Load(IEnumerable<(uint Id, string Name, string Zone, uint Territory, System.Numerics.Vector3? Position)> rows)
    {
        foreach (var (id, name, zone, territory, position) in rows)
        {
            if (name.Length == 0)
                continue;
            _byId[id] = (name, zone, territory, position);
            // Sheet-exact and canonical forms both, so either spelling in the data hits.
            _byName.TryAdd(name, id);
            _byName.TryAdd(Canon(name), id);
            _byName.TryAdd($"{Canon(zone)} - {Canon(name)}", id);
        }
        foreach (var (alias, real) in CityAliases)
            if (_byName.TryGetValue(real, out var id))
            {
                _byName[alias] = id;
                _aliasById[id] = alias;
            }
    }

    /// <summary>The name in the path data's own spelling — what the recorder writes so the resolver reads it back.</summary>
    public string DataName(uint aetheryteId)
    {
        if (_aliasById.TryGetValue(aetheryteId, out var alias))
            return alias;
        if (!_byId.TryGetValue(aetheryteId, out var v))
            return $"aetheryte {aetheryteId}";
        return v.Zone.Length > 0 && !Canon(v.Zone).Equals(Canon(v.Name), StringComparison.OrdinalIgnoreCase)
            ? $"{Canon(v.Zone)} - {Canon(v.Name)}"
            : Canon(v.Name);
    }

    /// <summary>The nearest aetheryte in a zone within <paramref name="maxDistance"/> of a point, or null.</summary>
    public uint? NearestIn(uint territoryId, System.Numerics.Vector3 point, float maxDistance)
    {
        uint? best = null;
        var bestDistance = maxDistance;
        foreach (var (id, v) in _byId)
        {
            if (v.TerritoryId != territoryId || v.Position is not { } p)
                continue;
            var d = System.Numerics.Vector3.Distance(p, point);
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = id;
            }
        }
        return best;
    }

    /// <summary>
    /// Every aetheryte in a zone, closest to <paramref name="near"/> first when a point is given.
    ///
    /// <para>
    /// Unlike <see cref="NearestIn"/> this has no distance limit: it answers "how would I get to
    /// this zone at all", where any aetheryte in it will do and the nearest is merely the least
    /// walking afterwards. Ones with no recorded position sort last rather than being dropped —
    /// an aetheryte we cannot place is still one we can teleport to.
    /// </para>
    /// </summary>
    public IReadOnlyList<uint> InTerritory(uint territoryId, System.Numerics.Vector3? near = null)
    {
        var found = new List<(uint Id, float Distance)>();
        foreach (var (id, v) in _byId)
        {
            if (v.TerritoryId != territoryId)
                continue;
            var distance = near is { } point && v.Position is { } p
                ? System.Numerics.Vector3.Distance(p, point)
                : float.MaxValue;
            found.Add((id, distance));
        }
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found.ConvertAll(f => f.Id);
    }

    /// <summary>The aethernet destination in a zone, nearest to a point when one is given.</summary>
    public Shard? ShardIn(uint territoryId, System.Numerics.Vector3? near = null)
    {
        Shard? best = null;
        var bestDistance = float.MaxValue;
        foreach (var shard in _shards)
        {
            if (shard.TerritoryId != territoryId) continue;
            var distance = near is { } point && shard.Position is { } p
                ? System.Numerics.Vector3.Distance(p, point)
                : float.MaxValue;
            if (best is null || distance < bestDistance)
            {
                best = shard;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>The city aetheryte an aethernet group hangs off, or null when it has none.</summary>
    public uint? HubOfGroup(byte group) => _hubByGroup.TryGetValue(group, out var id) ? id : null;

    /// <summary>
    /// An aethernet stop by the name the path data uses, brackets and all.
    ///
    /// <para>
    /// Two spellings have to be tried. "[Ul'dah] Goldsmiths' Guild" is the shard the sheet calls
    /// "Goldsmiths' Guild", so the bracketed city is noise. But "[Ul'dah] Aetheryte Plaza" is the
    /// city aetheryte, which the sheet calls "Ul'dah Aetheryte Plaza" — there the city is part of
    /// the name. Measured across the bundle: without the second attempt, all sixteen city plazas
    /// resolve to nothing, which is one in eight of every aethernet name it uses. A third attempt
    /// restores the article the data drops — "The Crystarium Aetheryte Plaza".
    /// </para>
    ///
    /// <para>
    /// The nine Firmament stops resolve to nothing on purpose: they are not in the Aetheryte sheet
    /// at all but on Ishgard's housing aethernet, which Lifestream reaches through different calls.
    /// Those fall through to the by-name gate and fail saying so.
    /// </para>
    /// </summary>
    public Shard? StopNamed(string dataName)
    {
        var (city, name) = SplitCity(dataName);
        if (name.Length == 0)
            return null;
        if (_stopsByName.TryGetValue(name, out var direct))
            return direct;
        if (city.Length == 0)
            return null;
        if (_stopsByName.TryGetValue($"{city} {name}", out var prefixed))
            return prefixed;
        // The data drops the article the sheet keeps: "[Crystarium] Aetheryte Plaza" against
        // "The Crystarium Aetheryte Plaza".
        return _stopsByName.TryGetValue($"The {city} {name}", out var articled) ? articled : null;
    }

    /// <summary>"[Ul'dah] Aetheryte Plaza" → ("Ul'dah", "Aetheryte Plaza"). No brackets, no city.</summary>
    public static (string City, string Name) SplitCity(string dataName)
    {
        var close = dataName.IndexOf(']');
        return dataName.StartsWith('[') && close > 0
            ? (dataName[1..close].Trim(), dataName[(close + 1)..].Trim())
            : (string.Empty, dataName.Trim());
    }

    /// <summary>The aethernet group a zone belongs to, or null when it is on no network.</summary>
    public byte? GroupOfTerritory(uint territoryId)
        => _groupByTerritory.TryGetValue(territoryId, out var g) ? g : null;

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

    public System.Numerics.Vector3? PositionOf(uint aetheryteId)
        => _byId.TryGetValue(aetheryteId, out var v) ? v.Position : null;

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
