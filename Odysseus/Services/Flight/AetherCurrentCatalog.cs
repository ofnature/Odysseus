using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Flight;

/// <summary>One aether current, and how it is obtained.</summary>
/// <param name="QuestId">The quest that grants it, or 0 when it is a pickup out in the world.</param>
/// <param name="Position">Where the pickup is: a path's step to it, else the zone's layout.</param>
/// <param name="DataId">The pickup's own object — what the attune presses.</param>
/// <param name="GiverTerritory">For a quest current: the zone its quest is taken in (0 when unknown).</param>
/// <param name="GiverAt">For a quest current: where its quest is taken.</param>
public sealed record AetherCurrent(uint Id, ushort QuestId, Vector3? Position, uint? DataId = null,
    uint GiverTerritory = 0, Vector3? GiverAt = null)
{
    public bool FromQuest => QuestId != 0;
    /// <summary>A pickup we know how to walk to.</summary>
    public bool IsReachable => !FromQuest && Position is not null;
}

/// <summary>A zone's currents and how far through it you are.</summary>
public sealed record ZoneFlight(uint TerritoryId, string Name, IReadOnlyList<AetherCurrent> Currents, int Unlocked)
{
    public int Total => Currents.Count;
    public bool CanFly => Total > 0 && Unlocked >= Total;
    public IEnumerable<AetherCurrent> Missing(Func<uint, bool> unlocked) => Currents.Where(c => !unlocked(c.Id));
}

/// <summary>
/// Which aether currents each zone needs, and where the loose ones are.
///
/// <para>
/// <c>AetherCurrentCompFlgSet</c> is the zone → currents mapping; <c>AetherCurrent.Quest</c> says
/// whether a current is handed over by a quest or has to be found on the ground. Flight needs all
/// of them, and the quest half is mostly side quests — which is why running the MSQ alone leaves
/// zones unflyable, and why this exists.
/// </para>
///
/// <para>
/// Positions for the loose ones are harvested from the converted paths first: every
/// <c>AttuneAetherCurrent</c> step carries both an id and a position the path has stood at. The
/// rest come from the zone's own layout (<c>planevent.lgb</c>), which places every current object —
/// an <c>EObj</c> whose <c>Data</c> is the current. Between them every loose current is placed.
/// A quest current's spot is its quest's giver (<c>Quest.IssuerLocation</c>).
/// </para>
/// </summary>
public sealed class AetherCurrentCatalog
{
    private readonly List<ZoneFlight> _zones = [];

    public AetherCurrentCatalog(IDataManager data, PathStore paths, Action<string> log)
    {
        try
        {
            var positions = HarvestPositions(paths);
            var laid = LayoutPositions(data);

            foreach (var set in data.GetExcelSheet<AetherCurrentCompFlgSet>())
            {
                var territory = set.Territory.RowId;
                if (territory == 0) continue;

                var currents = new List<AetherCurrent>();
                foreach (var slot in set.AetherCurrents)
                {
                    var id = slot.RowId;
                    if (id == 0) continue;
                    var questRow = slot.ValueNullable?.Quest.RowId ?? 0;
                    var questId = questRow >= Quest.QuestCatalog.RowIdBase
                        ? (ushort)(questRow - Quest.QuestCatalog.RowIdBase)
                        : (ushort)0;
                    var seen = positions.TryGetValue(id, out var where) ? where : default;
                    if (seen.Position is null && laid.TryGetValue(id, out var placed))
                        seen = (placed.Position, placed.DataId);
                    var giver = slot.ValueNullable?.Quest.ValueNullable?.IssuerLocation.ValueNullable;
                    currents.Add(questId == 0
                        ? new AetherCurrent(id, questId, seen.Position, seen.DataId)
                        : new AetherCurrent(id, questId, seen.Position, seen.DataId,
                            giver?.Territory.RowId ?? 0, giver is { } g ? new Vector3(g.X, g.Y, g.Z) : null));
                }
                if (currents.Count == 0) continue;

                var name = set.Territory.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? $"territory {territory}";
                _zones.Add(new ZoneFlight(territory, name, currents, 0));
            }
        }
        catch (Exception ex)
        {
            log($"Aether current catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test constructor.</summary>
    public AetherCurrentCatalog(IEnumerable<ZoneFlight> zones) => _zones.AddRange(zones);

    public IReadOnlyList<ZoneFlight> Zones => _zones;

    public ZoneFlight? ForTerritory(uint territoryId) => _zones.FirstOrDefault(z => z.TerritoryId == territoryId);

    /// <summary>Every zone, with the unlocked count filled in for this character.</summary>
    public IReadOnlyList<ZoneFlight> Progress(Func<uint, bool> unlocked)
        => _zones.Select(z => z with { Unlocked = z.Currents.Count(c => unlocked(c.Id)) }).ToList();

    /// <summary>
    /// Where every current object stands, read from each zone's event layout: an event object whose
    /// <c>EObj.Data</c> is an aether current. Covers the ones no path walks to.
    /// </summary>
    private static Dictionary<uint, (Vector3 Position, uint DataId)> LayoutPositions(IDataManager data)
    {
        var found = new Dictionary<uint, (Vector3 Position, uint DataId)>();
        var currentIds = data.GetExcelSheet<Lumina.Excel.Sheets.AetherCurrent>().Select(r => r.RowId).ToHashSet();
        var objects = data.GetExcelSheet<EObj>().Where(e => currentIds.Contains(e.Data.RowId))
            .ToDictionary(e => e.RowId, e => e.Data.RowId);
        foreach (var set in data.GetExcelSheet<AetherCurrentCompFlgSet>())
        {
            var bg = set.Territory.ValueNullable?.Bg.ExtractText() ?? string.Empty;
            var level = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (level < 0) continue;
            var layout = data.GetFile<Lumina.Data.Files.LgbFile>($"bg/{bg[..level]}/level/planevent.lgb");
            if (layout is null) continue;
            foreach (var layer in layout.Layers)
                foreach (var instance in layer.InstanceObjects)
                    if (instance.Object is Lumina.Data.Parsing.Layer.LayerCommon.EventInstanceObject eventObject
                        && objects.TryGetValue(eventObject.ParentData.BaseId, out var current))
                    {
                        var t = instance.Transform.Translation;
                        found.TryAdd(current, (new Vector3(t.X, t.Y, t.Z), eventObject.ParentData.BaseId));
                    }
        }
        return found;
    }

    /// <summary>
    /// Where the loose currents are, taken from every converted path. A current can appear in more
    /// than one path; the first position wins, since they are all the same object in the world.
    /// </summary>
    private static Dictionary<uint, (Vector3? Position, uint? DataId)> HarvestPositions(PathStore paths)
    {
        // The object too: a step built without it had nothing to press, walked to the spot, called
        // itself done and left the current locked (Lakeland, 0 of 2 collected).
        var found = new Dictionary<uint, (Vector3? Position, uint? DataId)>();
        foreach (var path in paths.All)
            foreach (var sequence in path.Sequences)
                foreach (var step in sequence.Steps)
                {
                    if (step.Kind != StepKind.AttuneAetherCurrent) continue;
                    if (step.AetherCurrentId is not { } id || step.Position is not { } position) continue;
                    if (!found.TryGetValue(id, out var have) || (have.DataId is null && step.DataId is not null))
                        found[id] = (position, step.DataId);
                }
        return found;
    }
}
