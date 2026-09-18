using System;
using System.Collections.Generic;
using System.Numerics;
using Odysseus.Services.Paths;

namespace Odysseus.Services.Quest;

/// <summary>One place the game's own journal data names: where it is, and what stands there.</summary>
/// <param name="Object">The ENpc or EObj placed there, or 0 when the game names a spot and nothing on it.</param>
public sealed record QuestMark(uint Territory, Vector3 Position, uint Object);

/// <summary>
/// A quest as the game's own sheets describe it: who gives it, who takes it back, and where each
/// journal objective points. This is <i>all</i> the sheets carry — no dialogue answers, no emotes,
/// no items to use, no duty ids, and no ordering between objectives that are live together.
/// </summary>
public sealed record QuestGeometry(
    ushort QuestId,
    string Name,
    QuestMark? Accept,
    uint AcceptNpc,
    QuestMark? TurnIn,
    uint TurnInNpc,
    IReadOnlyList<(byte Sequence, IReadOnlyList<QuestMark> Marks)> Objectives);

/// <summary>Reads <see cref="QuestGeometry"/> out of the game's sheets. Implemented over Lumina; faked in tests.</summary>
public interface IQuestGeometry
{
    QuestGeometry? Read(ushort questId);
}

/// <summary>
/// Turns what the game says about a quest into a path the ordinary step machinery can run.
///
/// <para>
/// 1,030 quests in the journal have objectives and no recorded path, and a run that cannot start
/// is worse than a run that gets most of the way and asks for help. Measured across the 4,240
/// paths that <i>are</i> recorded (Tools/PathAudit, 2026-09-17): the sheets name the accept NPC
/// and its position for every one of them, the turn-in for 99.9%, and a place for 99.7% of
/// objective sequences — with the sequence's <b>last</b> step within 25 yalms of a derived mark
/// 92.3% of the time. So roughly one sequence in thirteen lands in the right zone at the wrong
/// spot, and everything the sheets do not carry is simply absent.
/// </para>
///
/// <para>
/// Which is why a derived path is only ever walking, talking and interacting. Anything else a
/// quest wants — an emote, an item, a duty, a dialogue answer — is not guessed at: the steps run
/// out, the sequence does not move, and the run says the path was derived and what that means.
/// A derived path is never stored and never packed; recording one is still how a quest gets a
/// real path.
/// </para>
/// </summary>
public static class DerivedPath
{
    /// <summary>What <see cref="QuestPath.Category"/> carries so a derived path is recognisable everywhere.</summary>
    public const string Category = "Derived";

    /// <summary>ENpcResident ids start here — the quest givers and the people you talk to.</summary>
    private const uint NpcBase = 1_000_000;

    /// <summary>EObj ids start here; above the top of the range is not something to interact with.</summary>
    private const uint ObjectBase = 2_000_000;
    private const uint ObjectTop = 3_000_000;

    /// <summary>The path for a quest, or null when the sheets do not place enough of it to be worth running.</summary>
    public static QuestPath? Build(QuestGeometry quest)
    {
        var sequences = new List<QuestSequence>();

        if (quest.Accept is { } accept)
            sequences.Add(One(0, Step(StepKind.AcceptQuest, quest.AcceptNpc, accept)));

        foreach (var (sequence, marks) in quest.Objectives)
        {
            if (sequence is 0 or 255)
                continue;   // the ends are the two above; a to-do claiming them is not one
            var steps = new List<QuestStep>();
            foreach (var mark in marks)
                steps.Add(Step(KindOf(mark), Interactable(mark.Object) ? mark.Object : null, mark));
            if (steps.Count > 0)
                sequences.Add(new QuestSequence { Sequence = sequence, Steps = steps });
        }

        if (quest.TurnIn is { } turnIn)
            sequences.Add(One(255, Step(StepKind.CompleteQuest, quest.TurnInNpc, turnIn)));

        // Nothing but an accept is not a path: it takes the quest and then stands there. The
        // turn-in is what makes even a talk-to-one-person quest finishable.
        if (quest.TurnIn is null || sequences.Count < 2)
            return null;

        return new QuestPath
        {
            QuestId = quest.QuestId,
            Name = quest.Name,
            Category = Category,
            Author = "the game's own journal data",
            Sequences = sequences,
        };
    }

    private static QuestSequence One(byte sequence, QuestStep step)
        => new() { Sequence = sequence, Steps = [step] };

    private static QuestStep Step(StepKind kind, uint? dataId, QuestMark mark) => new()
    {
        Kind = kind,
        KindName = kind.ToString(),
        DataId = dataId is > 0 ? dataId : null,
        Position = mark.Position,
        TerritoryId = mark.Territory,
    };

    /// <summary>
    /// A mark with a person or a thing on it is interacted with; one with neither is walked to.
    /// An objective that points at an enemy or a region carries an id in neither range, and
    /// walking there is the honest half of what it wants — the fighting is not ours to invent.
    /// </summary>
    private static StepKind KindOf(QuestMark mark) => Interactable(mark.Object) ? StepKind.Interact : StepKind.WalkTo;

    private static bool Interactable(uint id) => id >= NpcBase && id < ObjectTop;
}

/// <summary>Derived paths, read once per quest and kept. Null everywhere when the setting is off.</summary>
public sealed class DerivedPaths
{
    private readonly IQuestGeometry _sheets;
    private readonly Func<bool> _enabled;
    private readonly Dictionary<ushort, QuestPath?> _cache = [];

    public DerivedPaths(IQuestGeometry sheets, Func<bool>? enabled = null)
    {
        _sheets = sheets;
        _enabled = enabled ?? (() => true);
    }

    public QuestPath? ForQuest(ushort questId)
    {
        if (!_enabled())
            return null;
        if (_cache.TryGetValue(questId, out var cached))
            return cached;
        var built = _sheets.Read(questId) is { } geometry ? DerivedPath.Build(geometry) : null;
        _cache[questId] = built;
        return built;
    }

    public bool Has(ushort questId) => ForQuest(questId) is not null;
}
