using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace Odysseus.Services.Quest;

/// <summary>What a quest can hand over, before anything is known about what it actually did.</summary>
public interface IQuestRewards
{
    /// <summary>
    /// Every item this quest might award — guaranteed and optional together — or empty when it is
    /// not a quest we sweep after.
    /// </summary>
    IReadOnlyList<uint> Candidates(ushort questId);
}

/// <summary>
/// Reward items from the <c>Quest</c> sheet, for the crafter and gatherer lines.
///
/// <para>
/// Only the <i>candidates</i> come from here, never the outcome. A quest's optional rewards are a
/// menu of four or five of which exactly one is taken, and the sheet cannot say which — so this
/// answers "what might have arrived" and the ledger measures what did. Treating the sheet as the
/// answer would mark four items you never received, and one of them could be something you were
/// holding for your own reasons.
/// </para>
///
/// <para>
/// Gated to Disciples of the Hand and Land, because that is the sweep that was asked for: those
/// lines are run at level 100 in finished gear, so their tools and dyed cotton smocks are landfill.
/// A combat quest's reward is not, and is left alone.
/// </para>
/// </summary>
public sealed class QuestRewards : IQuestRewards
{
    /// <summary>ClassJob rows 8–15 are CRP..CUL and 16–18 are MIN/BTN/FSH.</summary>
    private static bool IsHandOrLand(uint classJob) => classJob is >= 8 and <= 18;

    private readonly IDataManager _data;
    private readonly Action<string>? _log;
    private readonly Dictionary<ushort, uint[]> _cache = new();

    public QuestRewards(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public IReadOnlyList<uint> Candidates(ushort questId)
    {
        if (_cache.TryGetValue(questId, out var cached)) return cached;

        var result = Array.Empty<uint>();
        try
        {
            // The sheet keys quests at 0x10000 above the id everything else uses.
            if (_data.GetExcelSheet<Lumina.Excel.Sheets.Quest>().GetRowOrDefault(questId + 65536u) is { } row
                && IsHandOrLand(row.ClassJobRequired.RowId))
            {
                var ids = new List<uint>();
                foreach (var reward in row.Reward)
                    if (reward.RowId != 0) ids.Add(reward.RowId);
                foreach (var optional in row.OptionalItemReward)
                    if (optional.RowId != 0) ids.Add(optional.RowId);
                result = ids.ToArray();
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Reward lookup for quest {questId} failed: {ex.GetType().Name}: {ex.Message}");
        }

        return _cache[questId] = result;
    }
}
