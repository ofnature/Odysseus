using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace Odysseus.Services.Tribes;

/// <summary>One tribe's standing and today's dailies, as the game has them right now.</summary>
public sealed record TribeStanding(
    byte TribeId,
    byte Rank,
    ushort Reputation,
    ushort ReputationNeeded,
    /// <summary>Dailies of this tribe currently in the journal.</summary>
    IReadOnlyList<ushort> AcceptedDailies,
    /// <summary>Dailies of this tribe already turned in today (the game's daily-done slots).</summary>
    int CompletedToday)
{
    public bool Unlocked => Rank >= 1;
    /// <summary>Three dailies a day per tribe.</summary>
    public const int DailiesPerTribe = 3;
    public int TakenToday => AcceptedDailies.Count + CompletedToday;
    public int SlotsLeft => Math.Max(0, DailiesPerTribe - TakenToday);
}

/// <summary>Reads tribe state from the game. Interface so the runner can be tested.</summary>
public interface ITribeState
{
    TribeStanding Read(TribeInfo tribe);
    /// <summary>Daily allowances left across all tribes (12 a day).</summary>
    int AllowanceLeft { get; }
}

/// <summary>
/// The real reader: <c>PlayerState</c> for rank and reputation, <c>QuestManager</c> for the
/// allowance, the accepted dailies (journal entries whose quest belongs to the tribe) and today's
/// completed ones (the daily-done slots). All read live; nothing cached — the whole point is that
/// it is always right.
/// </summary>
public sealed unsafe class TribeState : ITribeState
{
    private readonly TribeCatalog _catalog;
    private readonly Action<string>? _log;

    public TribeState(TribeCatalog catalog, Action<string>? log = null)
    {
        _catalog = catalog;
        _log = log;
    }

    public int AllowanceLeft
    {
        get
        {
            try
            {
                var qm = QuestManager.Instance();
                return qm == null ? 0 : (int)qm->GetBeastTribeAllowance();
            }
            catch
            {
                return 0;
            }
        }
    }

    public TribeStanding Read(TribeInfo tribe)
    {
        try
        {
            var ps = PlayerState.Instance();
            var qm = QuestManager.Instance();
            if (ps == null || qm == null)
                return new TribeStanding(tribe.Id, 0, 0, 0, Array.Empty<ushort>(), 0);

            var rank = ps->GetBeastTribeRank(tribe.Id);
            var rep = ps->GetBeastTribeCurrentReputation(tribe.Id);
            var need = ps->GetBeastTribeNeededReputation(tribe.Id);

            var accepted = new List<ushort>();
            foreach (ref readonly var work in qm->NormalQuests)
            {
                if (work.QuestId == 0) continue;
                if (tribe.DailyQuestIds.Contains(work.QuestId))
                    accepted.Add(work.QuestId);
            }

            var doneToday = 0;
            foreach (ref readonly var daily in qm->DailyQuests)
            {
                if (daily.QuestId == 0) continue;
                if (tribe.DailyQuestIds.Contains(daily.QuestId) && daily.IsCompleted)
                    doneToday++;
            }

            return new TribeStanding(tribe.Id, rank, rep, need, accepted, doneToday);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Tribe state read failed for {tribe.Name}: {ex.Message}");
            return new TribeStanding(tribe.Id, 0, 0, 0, Array.Empty<ushort>(), 0);
        }
    }
}
