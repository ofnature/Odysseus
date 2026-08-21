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
    ushort[] Previous, byte PreviousJoin, ushort[] Locks, byte LockJoin)
{
    public QuestListing(ushort questId, string name, ushort classJobLevel, uint expansionId, bool isMainScenario, ushort[] previous, byte previousJoin)
        : this(questId, name, classJobLevel, expansionId, isMainScenario, previous, previousJoin, [], 0) { }

    /// <summary>
    /// <c>ClassJobCategory0</c> — which classes may take the quest at all. Kept as the id rather
    /// than the name because the wide categories are the interesting ones here and
    /// <see cref="JobName"/> deliberately throws those away.
    /// </summary>
    public uint ClassCategoryId { get; init; }

    /// <summary>
    /// The quest can only be taken as a Disciple of the Hand or Land. Category 35, which is what
    /// every custom delivery client's unlock quest carries (checked 2026-08-20 across all twelve).
    /// </summary>
    public bool NeedsHandOrLand => ClassCategoryId == DisciplesOfLandOrHand;

    /// <summary><c>ClassJobCategory</c> row for "Disciples of the Land or Hand".</summary>
    public const uint DisciplesOfLandOrHand = 35;

    /// <summary>
    /// The journal grouping this quest sits under — "Chronicles of a New Era", "Sidequests" and so
    /// on. Kept because it is the only thing that identifies a raid or trial unlock chain as such;
    /// the engine runs any quest, but nothing could <i>find</i> them without this.
    /// </summary>
    public string Section { get; init; } = string.Empty;

    /// <summary>The category within the section — an alliance raid series, a job's quest line.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>
    /// The class the quest belongs to, when it belongs to exactly one — "Carpenter", "Miner".
    /// The journal lumps every crafter into one category of 126 quests, which is unreadable; this
    /// is what splits it back apart.
    /// </summary>
    public string JobName { get; init; } = string.Empty;

    /// <summary>The prerequisites are met, given a completion oracle.</summary>
    public bool IsUnlockedBy(Func<ushort, bool> isComplete) => PreviousJoin switch
    {
        1 => Previous.All(isComplete),
        2 => Previous.Length == 0 || Previous.Any(isComplete),
        _ => true,
    };

    /// <summary>
    /// A mutually exclusive alternative has been taken (<c>QuestLock</c>): the three Grand Company
    /// quests lock each other, and so on. Join 1 = all locks complete, 2 = any.
    /// </summary>
    public bool IsLockedOutBy(Func<ushort, bool> isComplete) => Locks.Length > 0 && LockJoin switch
    {
        1 => Locks.All(isComplete),
        2 => Locks.Any(isComplete),
        _ => false,
    };
}

/// <summary>
/// What the character is, for the story-frontier rules that the sheet does not encode. Read
/// from <c>PlayerState</c>; zeros mean unknown and disable the rule.
/// </summary>
/// <param name="StartTown">1 Limsa, 2 Gridania, 3 Ul'dah.</param>
/// <param name="FirstClass">ClassJob id the character was created as.</param>
/// <param name="GrandCompany">1 Maelstrom, 2 Twin Adder, 3 Immortal Flames; 0 not yet joined.</param>
/// <param name="PreferredGrandCompany">The user's choice for when the story asks (same ids); 0 = none set.</param>
public readonly record struct CharacterFacts(byte StartTown, byte FirstClass, byte GrandCompany, byte PreferredGrandCompany)
{
    public static CharacterFacts Unknown => default;
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
            var jobNames = ClassNamesByAbbreviation(data);
            var rows = new List<QuestListing>();
            foreach (var row in data.GetExcelSheet<Lumina.Excel.Sheets.Quest>())
            {
                if (row.RowId < RowIdBase)
                    continue;

                var name = row.Name.ExtractText();
                if (string.IsNullOrEmpty(name))
                    continue;

                var isMsq = false;
                var sectionName = string.Empty;
                var categoryName = string.Empty;
                if (row.JournalGenre.ValueNullable is { } genre
                    && genre.JournalCategory.ValueNullable is { } category
                    && category.JournalSection.ValueNullable is { } section)
                {
                    sectionName = section.Name.ExtractText();
                    categoryName = category.Name.ExtractText();
                    isMsq = section.RowId <= LastMainScenarioSection
                            || sectionName.Contains("Main Scenario", StringComparison.OrdinalIgnoreCase);
                }

                var previous = row.PreviousQuest
                    .Where(p => p.RowId >= RowIdBase)
                    .Select(p => (ushort)(p.RowId - RowIdBase))
                    .ToArray();
                var locks = row.QuestLock
                    .Where(p => p.RowId >= RowIdBase)
                    .Select(p => (ushort)(p.RowId - RowIdBase))
                    .ToArray();

                var id = (ushort)(row.RowId - RowIdBase);
                rows.Add(new QuestListing(id, name, row.ClassJobLevel[0], row.Expansion.RowId, isMsq, previous,
                    row.PreviousQuestJoin, locks, row.QuestLockJoin)
                {
                    Section = sectionName,
                    Category = categoryName,
                    JobName = JobOf(row, jobNames),
                    ClassCategoryId = row.ClassJobCategory0.RowId,
                });
            }
            Load(rows);
        }
        catch (Exception ex)
        {
            log($"Quest catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Which single class a quest belongs to, or empty when it is not class-specific.
    ///
    /// <para>
    /// <c>ClassJobRequired</c> names the class outright and is preferred. Failing that,
    /// <c>ClassJobCategory0</c> is used — but only when its name is short enough to be one class's
    /// abbreviation. The wide categories ("Disciples of the Hand", "All Classes") share the same
    /// column and would group everything back together, which is the problem this exists to solve.
    /// </para>
    ///
    /// <para>
    /// The fallback is then resolved back through <c>ClassJob.Abbreviation</c>, because the two
    /// columns disagree on spelling for the same class: a class's opening quest gives "CRP" where
    /// the rest of its line gives "Carpenter", and unresolved that shows up as a group of one
    /// sitting beside a group of twenty.
    /// </para>
    /// </summary>
    private static string JobOf(Lumina.Excel.Sheets.Quest row, IReadOnlyDictionary<string, string> byAbbreviation)
    {
        if (row.ClassJobRequired.ValueNullable?.Name.ExtractText() is { Length: > 0 } required)
            return Capitalise(required);

        var category = row.ClassJobCategory0.ValueNullable?.Name.ExtractText() ?? string.Empty;
        if (category.Length is 0 or > 4) return string.Empty;
        return byAbbreviation.TryGetValue(category, out var full) ? full : category;
    }

    /// <summary>Class abbreviation → full name, e.g. CRP → Carpenter.</summary>
    private static Dictionary<string, string> ClassNamesByAbbreviation(IDataManager data)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>())
        {
            var abbreviation = job.Abbreviation.ExtractText();
            var name = job.Name.ExtractText();
            if (abbreviation.Length > 0 && name.Length > 0)
                map.TryAdd(abbreviation, Capitalise(name));
        }
        return map;
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

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

    /// <summary>Name search, case-insensitive contains; MSQ and lower ids first, capped.</summary>
    /// <summary>Every quest the sheets know, for grouping by journal section.</summary>
    public IEnumerable<QuestListing> All => _byId.Values;

    public IEnumerable<QuestListing> Search(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<QuestListing>();
        return _byId.Values
            .Where(q => q.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(q => q.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(q => q.IsMainScenario)
            .ThenBy(q => q.QuestId)
            .Take(max);
    }

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
        => NextMainScenario(completed, isComplete, CharacterFacts.Unknown);

    public ushort? NextMainScenario(ushort completed, Func<ushort, bool> isComplete, CharacterFacts facts)
    {
        var direct = Ready(completed, isComplete, facts);
        if (direct is not null)
            return direct;

        if (_byId.TryGetValue(completed, out var me))
            foreach (var parent in me.Previous)
                if (Ready(parent, isComplete, facts) is { } sibling)
                    return sibling;

        return null;
    }

    // ── the rules the sheet does not carry (from QuestFlow's reading of the same problem; ids verified against the sheet) ──

    /// <summary>The three city-start roots, by <c>PlayerState.StartTown</c>.</summary>
    private static readonly Dictionary<ushort, byte> RootTown = new() { [107] = 1, [39] = 2, [594] = 3 };

    /// <summary>"Close to Home" class variants: the NPC offers all three, the game takes the one for the character's first class.</summary>
    private static readonly Dictionary<ushort, byte> CloseToHomeClass = new()
    {
        [108] = 3 /* MRD */, [109] = 26 /* ACN */,
        [85] = 4 /* LNC */, [123] = 5 /* ARC */, [124] = 6 /* CNJ */,
        [568] = 1 /* GLA */, [569] = 2 /* PGL */, [570] = 7 /* THM */,
    };

    /// <summary>"The Company You Keep" — one per Grand Company; the choice is the character's, or the user's setting.</summary>
    private static readonly Dictionary<ushort, byte> CompanyQuest = new() { [681] = 1 /* Maelstrom */, [680] = 2 /* Twin Adder */, [682] = 3 /* Immortal Flames */ };

    /// <summary>Whether this character can ever take the quest, beyond prerequisites.</summary>
    public static bool IsObtainable(QuestListing q, Func<ushort, bool> isComplete, CharacterFacts facts)
    {
        if (q.IsLockedOutBy(isComplete))
            return false;
        if (RootTown.TryGetValue(q.QuestId, out var town) && facts.StartTown != 0 && facts.StartTown != town)
            return false;
        if (CloseToHomeClass.TryGetValue(q.QuestId, out var cls) && facts.FirstClass != 0 && facts.FirstClass != cls)
            return false;
        if (CompanyQuest.TryGetValue(q.QuestId, out var gc))
        {
            var want = facts.GrandCompany != 0 ? facts.GrandCompany : facts.PreferredGrandCompany;
            if (want != 0 && want != gc)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The character's Main Scenario frontier: the MSQ quest to do next, whether or not it is
    /// accepted. Null when the story is finished or nothing is unlocked.
    ///
    /// <para>
    /// A frontier quest is MSQ, not complete, has its prerequisites met, has at least one
    /// prerequisite (so the three "Coming to &lt;city&gt;" roots are not offered to a character
    /// who already started elsewhere), and has no completed successor — that last clause is what
    /// drops the untaken alternates: the other two "Close to Home" class variants, the two Grand
    /// Companies not joined, the 2.x branch not chosen. Ties go to the lowest id.
    /// </para>
    /// </summary>
    public QuestListing? CurrentMainScenario(Func<ushort, bool> isComplete)
        => CurrentMainScenario(isComplete, CharacterFacts.Unknown);

    public QuestListing? CurrentMainScenario(Func<ushort, bool> isComplete, CharacterFacts facts)
    {
        QuestListing? best = null;
        var anyDone = false;
        foreach (var q in _byId.Values)
        {
            if (!q.IsMainScenario) continue;
            if (isComplete(q.QuestId)) { anyDone = true; continue; }
            if (q.Previous.Length == 0 || !q.IsUnlockedBy(isComplete)) continue;
            if (!IsObtainable(q, isComplete, facts)) continue;
            if (_msqSuccessors.TryGetValue(q.QuestId, out var next) && next.Any(isComplete)) continue;
            if (best is null || q.QuestId < best.QuestId) best = q;
        }
        if (best is not null || anyDone)
            return best;

        // Brand-new character: nothing done at all. Offer the root for the start town, else the lowest.
        return _byId.Values
            .Where(q => q.IsMainScenario && q.Previous.Length == 0 && IsObtainable(q, isComplete, facts))
            .OrderBy(q => q.QuestId)
            .FirstOrDefault();
    }

    private ushort? Ready(ushort of, Func<ushort, bool> isComplete, CharacterFacts facts)
    {
        if (!_msqSuccessors.TryGetValue(of, out var successors))
            return null;
        ushort? best = null;
        foreach (var id in successors)
        {
            if (isComplete(id) || !_byId.TryGetValue(id, out var q) || !q.IsUnlockedBy(isComplete))
                continue;
            if (!IsObtainable(q, isComplete, facts))
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
