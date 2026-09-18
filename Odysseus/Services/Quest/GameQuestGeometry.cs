using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Quest;

/// <summary>
/// <see cref="IQuestGeometry"/> over the game's own sheets.
///
/// <para>
/// <c>Quest.IssuerLocation</c> places the giver, <c>Quest.TargetEnd</c> names the taker, and each
/// <c>Quest.TodoParams</c> entry carries the sequence it completes and the <c>Level</c> rows the
/// journal points at. A <c>Level</c> row is a territory, a position and the id of whatever stands
/// there. None of this is documented; it was found and then measured against the whole recorded
/// library with <c>Tools/PathAudit</c>, which reads the same sheets outside the game.
/// </para>
///
/// <para>
/// The one thing the quest sheet does not carry is where the <i>turn-in</i> NPC stands. Indexing
/// the Level sheet by what it places would answer it, and costs seconds — far too long to spend on
/// the game's frame thread. The journal's own to-do list answers it for free: measured across all
/// 5,366 quests with a placed turn-in (Tools/PathAudit --turnins), 96.6% carry a sequence-255 to-do
/// pointing at it, and 99% of those agree with the Level sheet to within a yalm. Another 82 are
/// handed back to whoever gave them, where the giver's own position serves. The remaining 102 —
/// 1.9% — are left to the recorder, which is where they already were.
/// </para>
/// </summary>
public sealed class GameQuestGeometry : IQuestGeometry
{
    /// <summary>Quest row ids are the quest number plus this.</summary>
    private const uint RowBase = 65536;

    private readonly IDataManager _data;
    private readonly Action<string> _log;

    public GameQuestGeometry(IDataManager data, Action<string> log)
    {
        _data = data;
        _log = log;
    }

    public QuestGeometry? Read(ushort questId)
    {
        try
        {
            if (_data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(RowBase + questId) is not { } quest)
                return null;

            var name = quest.Name.ExtractText();
            if (string.IsNullOrEmpty(name))
                return null;

            var accept = quest.IssuerLocation.ValueNullable is { } issued ? MarkOf(issued) : null;
            var turnInNpc = quest.TargetEnd.RowId;

            // Objectives are grouped by the sequence they complete; a sequence can name several
            // places, and the sheet's order is the only order there is.
            var bySequence = new Dictionary<byte, List<QuestMark>>();
            var order = new List<byte>();
            foreach (var todo in quest.TodoParams)
            {
                var sequence = todo.ToDoCompleteSeq;
                if (sequence == 0)
                    continue;
                foreach (var level in todo.ToDoLocation)
                {
                    if (level.ValueNullable is not { } row || MarkOf(row) is not { } mark)
                        continue;
                    if (!bySequence.TryGetValue(sequence, out var marks))
                    {
                        bySequence[sequence] = marks = [];
                        order.Add(sequence);
                    }
                    marks.Add(mark);
                }
            }

            // Sequence 255 is the hand-back, not an objective: its to-do is where the turn-in NPC
            // stands, which the quest sheet itself never says.
            var turnIn = bySequence.TryGetValue(255, out var end) && end.Count > 0 ? end[0]
                : turnInNpc != 0 && turnInNpc == quest.IssuerStart.RowId ? accept
                : null;

            order.Sort();
            var objectives = new List<(byte, IReadOnlyList<QuestMark>)>(order.Count);
            foreach (var sequence in order)
                objectives.Add((sequence, bySequence[sequence]));

            return new QuestGeometry(questId, name, accept, quest.IssuerStart.RowId, turnIn, turnInNpc, objectives);
        }
        catch (Exception ex)
        {
            _log($"Reading quest {questId} from the sheets failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static QuestMark? MarkOf(Level level)
        => level.Territory.RowId == 0 ? null : new QuestMark(level.Territory.RowId, new Vector3(level.X, level.Y, level.Z), level.Object.RowId);
}
