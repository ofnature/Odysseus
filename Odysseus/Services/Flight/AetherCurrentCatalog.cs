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
/// <param name="Position">Where the pickup is, when a path recorded it.</param>
public sealed record AetherCurrent(uint Id, ushort QuestId, Vector3? Position)
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
/// Positions for the loose ones are harvested from the converted paths: every
/// <c>AttuneAetherCurrent</c> step carries both an id and a position, so the corpus already knows
/// where they are without a table of our own. Anything no path has visited is listed without a
/// position and has to be collected by hand.
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
                    currents.Add(new AetherCurrent(id, questId, positions.GetValueOrDefault(id)));
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
    /// Where the loose currents are, taken from every converted path. A current can appear in more
    /// than one path; the first position wins, since they are all the same object in the world.
    /// </summary>
    private static Dictionary<uint, Vector3> HarvestPositions(PathStore paths)
    {
        var found = new Dictionary<uint, Vector3>();
        foreach (var path in paths.All)
            foreach (var sequence in path.Sequences)
                foreach (var step in sequence.Steps)
                {
                    if (step.Kind != StepKind.AttuneAetherCurrent) continue;
                    if (step.AetherCurrentId is not { } id || step.Position is not { } position) continue;
                    found.TryAdd(id, position);
                }
        return found;
    }
}
