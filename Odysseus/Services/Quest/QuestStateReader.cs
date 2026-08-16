using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Odysseus.Services.Quest;

/// <summary>
/// The real reader: <c>QuestManager</c> straight out of ClientStructs.
///
/// <para>
/// Verified against the pinned FFXIVClientStructs (Dalamud dev libs, 2026-08-15):
/// <c>QuestManager.NormalQuests</c> is a 30-slot span of
/// <c>Application.Network.WorkDefinitions.QuestWork</c>, each
/// <c>{ QuestId: ushort @8, Sequence: byte @10, Variables: 6 bytes @12 }</c>. Empty slots have
/// <c>QuestId == 0</c>. <c>IsQuestComplete</c> is static (completed-quest bitfield);
/// <c>IsQuestAccepted</c> / <c>GetQuestById</c> are instance methods on the singleton.
/// </para>
///
/// <para>
/// <b>Quest ids are the game's ushort form</b> (e.g. 1622), i.e. the Excel row id minus 65536.
/// The path bundle names its files with the same form, so nothing translates.
/// </para>
///
/// <para>
/// Every read copies the six variable bytes out of game memory into a fresh array, so a snapshot
/// stays valid after the frame ends. Reads happen on the framework thread; the caller owns that.
/// </para>
/// </summary>
public sealed unsafe class QuestStateReader : IQuestStateReader
{
    private readonly Action<string>? _logFault;
    private string _lastFault = string.Empty;

    /// <param name="logFault">
    /// Optional sink for read failures, called only when the failure changes — this can run every
    /// frame, so an unconditional log would flood.
    /// </param>
    public QuestStateReader(Action<string>? logFault = null) => _logFault = logFault;

    public QuestSnapshot Read(ushort questId)
    {
        if (questId == 0)
            return QuestSnapshot.Unavailable;

        try
        {
            var manager = QuestManager.Instance();
            if (manager == null)
                return Fault("QuestManager.Instance() is null");

            var work = manager->GetQuestById(questId);
            if (work == null || work->QuestId != questId)
                return QuestSnapshot.Unavailable;

            ClearFault();
            return new QuestSnapshot(work->QuestId, work->Sequence, work->Variables.ToArray());
        }
        catch (Exception ex)
        {
            return Fault($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public IReadOnlyList<QuestSnapshot> ReadAccepted()
    {
        try
        {
            var manager = QuestManager.Instance();
            if (manager == null)
            {
                Fault("QuestManager.Instance() is null");
                return Array.Empty<QuestSnapshot>();
            }

            var quests = manager->NormalQuests;
            var result = new List<QuestSnapshot>(manager->NumAcceptedQuests);
            foreach (ref readonly var work in quests)
            {
                if (work.QuestId == 0)
                    continue;
                result.Add(new QuestSnapshot(work.QuestId, work.Sequence, work.Variables.ToArray()));
            }

            ClearFault();
            return result;
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
            return Array.Empty<QuestSnapshot>();
        }
    }

    public bool IsComplete(ushort questId)
    {
        if (questId == 0)
            return false;
        try
        {
            return QuestManager.IsQuestComplete(questId);
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool IsAccepted(ushort questId)
    {
        if (questId == 0)
            return false;
        try
        {
            var manager = QuestManager.Instance();
            return manager != null && manager->IsQuestAccepted(questId);
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// <c>AgentScenarioTree</c> — the Scenario Guide's own pointer. Verified against the pinned
    /// ClientStructs 2026-08-15: <c>Data->MainScenarioQuestIds[Data->MSQPathIndex]</c> (slots 0–2
    /// are the paths, 3 is the last completed when nothing is accepted). This is the primary
    /// frontier source; the catalog's chain walk is the fallback, as it is in QuestFlow.
    /// </summary>
    public ushort? CurrentScenarioQuest()
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentScenarioTree.Instance();
            if (agent == null || agent->Data == null)
                return null;
            var data = agent->Data;
            var ids = data->MainScenarioQuestIds;
            var index = data->MSQPathIndex;
            if (index >= ids.Length || index > 2)
                index = 0;
            var id = ids[index];
            return id == 0 ? null : id;
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public CharacterFacts Character()
    {
        try
        {
            var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            if (state == null)
                return CharacterFacts.Unknown;
            return new CharacterFacts(state->StartTown, state->FirstClass, state->GrandCompany, 0);
        }
        catch
        {
            return CharacterFacts.Unknown;
        }
    }

    private QuestSnapshot Fault(string reason)
    {
        if (reason != _lastFault)
        {
            _lastFault = reason;
            _logFault?.Invoke(reason);
        }
        return QuestSnapshot.Unavailable;
    }

    private void ClearFault() => _lastFault = string.Empty;
}
