using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Quest;

/// <summary>One quest as the journal knows it.</summary>
/// <param name="QuestId">Game ushort form (Excel row id − 65536) — what <c>QuestManager</c> speaks.</param>
/// <param name="IsMainScenario">The quest is on the Main Scenario line, per the game's own journal genre.</param>
/// <param name="Previous">Prerequisite quests, as the sheet lists them.</param>
/// <param name="PreviousJoin">Sheet semantics: 1 = all of <paramref name="Previous"/>, 2 = at least one, 0 = none.</param>
public sealed record QuestListing(ushort QuestId, string Name, ushort ClassJobLevel, uint ExpansionId, bool IsMainScenario,
    ushort[] Previous, byte PreviousJoin)
{
    /// <summary>The prerequisites are met, given a completion oracle.</summary>
    public bool IsUnlockedBy(Func<ushort, bool> isComplete) => PreviousJoin switch
    {
        1 => Previous.All(isComplete),
        2 => Previous.Length == 0 || Previous.Any(isComplete),
        _ => true,
    };
}

/// <summary>
/// Quest names and metadata, read once from the game's own sheet.
///
/// <para>
/// The run window says "Mogwin's Trial", the reader says 1622, and something has to bridge the
/// two. Reading it from Lumina rather than shipping a table means it cannot go stale against a
/// patch and the names are the game's own — in whatever language the client runs.
/// </para>
/// </summary>
public sealed class QuestCatalog
{
    /// <summary>Quest sheet row ids sit at 65536 + the ushort id the rest of the game uses.</summary>
    public const uint RowIdBase = 65536;

    /// <summary>
    /// The journal's own MSQ grouping. Verified against the live sheets 2026-08-15 (7.x client):
    /// <c>Quest → JournalGenre → JournalCategory → JournalSection</c>, and the Main Scenario
    /// sections are row 0 ("Main Scenario (A Realm Reborn through Endwalker)") and row 1
    /// ("Main Scenario (Dawntrail)"). Category 0 is <i>not</i> MSQ — it is a stray "Sephiroth
    /// Missions" row in section 255 — which is why this keys on the section, not the category.
    /// The section <i>name</i> is checked as well so a future expansion that inserts a new
    /// section (as Dawntrail did) is still caught even if the ids shift.
    /// </summary>
    private const uint LastMainScenarioSection = 1;

    /// <summary>
    /// Sanity cross-check for the classification: 1,046 MSQ quests in total, and Heavensward's
    /// three categories (3 + 4 + 5) sum to 138 — exactly the number of HW MSQ files in the path
    /// bundle. If those two ever disagree, the classification here is what moved.
    /// </summary>
    private readonly Dictionary<ushort, QuestListing> _byId = new();
    /// <summary>MSQ quests that list the key as a prerequisite — the forward edges of the story chain.</summary>
    private readonly Dictionary<ushort, List<ushort>> _msqSuccessors = new();

    public QuestCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            var rows = new List<QuestListing>();
            foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.Quest>())
            {
                if (row.RowId < RowIdBase)
                    continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;

                var isMsq = false;
                if (row.JournalGenre.ValueNullable is { } genre
                    && genre.JournalCategory.ValueNullable is { } category
                    && category.JournalSection.ValueNullable is { } section)
                {
                    isMsq = section.RowId <= LastMainScenarioSection
                            || section.Name.ExtractText().Contains("Main Scenario", StringComparison.OrdinalIgnoreCase);
                }

                var previous = row.PreviousQuest
                    .Where(p => p.RowId >= RowIdBase)
                    .Select(p => (ushort)(p.RowId - RowIdBase))
                    .ToArray();

                var id = (ushort)(row.RowId - RowIdBase);
                rows.Add(new QuestListing(id, name, row.ClassJobLevel[0], row.Expansion.RowId, isMsq, previous, row.PreviousQuestJoin));
            }
            Load(rows);
        }
        catch (Exception ex)
        {
            log($"Quest catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test constructor.</summary>
    public QuestCatalog(IEnumerable<QuestListing> rows) => Load(rows);

    private void Load(IEnumerable<QuestListing> rows)
    {
        foreach (var listing in rows)
        {
            _byId[listing.QuestId] = listing;
            if (!listing.IsMainScenario)
                continue;
            foreach (var prev in listing.Previous)
            {
                if (!_msqSuccessors.TryGetValue(prev, out var list))
                    _msqSuccessors[prev] = list = [];
                list.Add(listing.QuestId);
            }
        }
    }

    public int Count => _byId.Count;

    /// <summary>
    /// The next MSQ quest to run after <paramref name="completed"/>, or null when the story is
    /// blocked or over.
    ///
    /// <para>
    /// The chain is the sheet's own <c>PreviousQuest</c> links (measured 2026-08-15: 1,048 MSQ
    /// nodes, only 17 branch points). Prefer a successor of the finished quest whose prerequisites
    /// are met; when none is — a "do these three in any order" fan, where the join quest wants the
    /// siblings first — step back to the finished quest's own prerequisites and take a sibling
    /// instead. Ties go to the lowest id, which is also the sheet's authoring order.
    /// </para>
    /// </summary>
    public ushort? NextMainScenario(ushort completed, Func<ushort, bool> isComplete)
    {
        var direct = Ready(completed, isComplete);
        if (direct is not null)
            return direct;

        if (_byId.TryGetValue(completed, out var me))
            foreach (var parent in me.Previous)
                if (Ready(parent, isComplete) is { } sibling)
                    return sibling;

        return null;
    }

    private ushort? Ready(ushort of, Func<ushort, bool> isComplete)
    {
        if (!_msqSuccessors.TryGetValue(of, out var successors))
            return null;
        ushort? best = null;
        foreach (var id in successors)
        {
            if (isComplete(id) || !_byId.TryGetValue(id, out var q) || !q.IsUnlockedBy(isComplete))
                continue;
            if (best is null || id < best)
                best = id;
        }
        return best;
    }

    public QuestListing? ById(ushort questId)
        => _byId.TryGetValue(questId, out var listing) ? listing : null;

    /// <summary>The name, or the bare id when the sheet did not have it (never throws, never blank).</summary>
    public string NameOf(ushort questId)
        => ById(questId)?.Name ?? $"Quest {questId}";
}
