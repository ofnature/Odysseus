using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Quest;

/// <summary>What a quest's <c>Duty</c> step points at, as the Duty Finder knows it.</summary>
/// <param name="IsDungeon">4-player dungeon — the only content Theseus runs.</param>
/// <param name="PartySize">Players the duty is designed for (4, 8, 24).</param>
public sealed record DutyDescription(uint ContentFinderConditionId, string Name, bool IsDungeon, int PartySize)
{
    public string Kind => IsDungeon ? "dungeon" : PartySize >= 24 ? "alliance raid" : PartySize >= 8 ? "8-player trial" : "duty";
}

/// <summary>
/// ContentFinderCondition → what kind of thing it is, read once from the game's sheets.
///
/// <para>
/// Exists for one decision: whether a <c>Duty</c> step is a dungeon (hand to Theseus) or a trial
/// (stop and say so). Measured across HW+SB MSQ 2026-08-15: 26 duty steps, 17 dungeons and 9
/// eight-player trials — and upstream flags every one of the trials as not automatable either.
/// Reading the sheet means the message names the trial rather than guessing from an id.
/// </para>
/// </summary>
public sealed class DutyCatalog
{
    /// <summary>ContentType row for Dungeons — the same constant Theseus's catalog keys on.</summary>
    private const uint DungeonContentType = 2;

    private readonly Dictionary<uint, DutyDescription> _byId = new();

    public DutyCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            foreach (var row in data.GetExcelSheet<ContentFinderCondition>())
            {
                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;
                var members = row.ContentMemberType.ValueNullable;
                var partySize = members is { } m ? Math.Max(1, m.MembersPerParty * Math.Max((int)m.PartyCount, 1)) : 0;
                _byId[row.RowId] = new DutyDescription(row.RowId, name, row.ContentType.RowId == DungeonContentType, partySize);
            }
        }
        catch (Exception ex)
        {
            log($"Duty catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test constructor.</summary>
    public DutyCatalog(IEnumerable<DutyDescription> rows)
    {
        foreach (var r in rows) _byId[r.ContentFinderConditionId] = r;
    }

    public DutyDescription? Describe(uint contentFinderConditionId)
        => _byId.TryGetValue(contentFinderConditionId, out var d) ? d : null;
}
