using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>One custom-delivery client, as the sheets describe it.</summary>
/// <param name="Index">SatisfactionNpc row id.</param>
/// <param name="UnlockQuestId">The quest that opens this client — every one has prerequisites of its own.</param>
/// <param name="NpcDataId">The ENpcResident id to walk up to and interact with.</param>
public sealed record DeliveryClient(
    uint Index, string Name, int DeliveriesPerWeek, ushort UnlockQuestId, ushort UnlockLevel,
    uint TerritoryId, uint NpcDataId = 0, System.Numerics.Vector3 Position = default);

/// <summary>Reads the delivery clients' standing from the game.</summary>
public interface IDeliveryState
{
    bool IsUnlocked(DeliveryClient client);
    /// <summary>Deliveries already used this week, or null when it cannot be read.</summary>
    int? UsedThisWeek(DeliveryClient client);
    int Rank(DeliveryClient client);

    /// <summary>
    /// The satisfaction gauge: how far into the current rank, and what the rank needs. Max is 0 at
    /// the top rank, where there is nothing left to fill.
    /// </summary>
    (int Current, int Max) Satisfaction(DeliveryClient client);

    /// <summary>
    /// The client has fetched delivery data from the server. Ranks and weekly allowances read as
    /// zero until it has — opening the game's Custom Deliveries window once is what loads them —
    /// so the UI can say "not loaded yet" instead of showing zeros as though they were facts.
    /// </summary>
    bool DataLoaded { get; }
}

/// <summary>
/// The custom-delivery clients, read from <c>SatisfactionNpc</c>.
///
/// <para>
/// Verified 2026-08-16: twelve clients, six deliveries a week each, and each carries
/// <c>QuestRequired</c> — the quest that unlocks it (Zhloe ← "Arms Wide Open", Kai-Shirr ←
/// "Oh, Beehive Yourself", …). Every one of those unlock quests has prerequisites of its own, so
/// an Unlock button has to run a chain, not a quest — see <see cref="Quest.QuestChain"/>.
/// </para>
///
/// <para>Running deliveries (buy → craft → turn in) is P8; this is the unlock half.</para>
/// </summary>
public sealed class DeliveryCatalog
{
    private readonly List<DeliveryClient> _clients = [];

    public DeliveryCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            var quests = data.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
            foreach (var npc in data.GetExcelSheet<SatisfactionNpc>())
            {
                if (npc.RowId == 0) continue;
                var name = npc.Npc.ValueNullable?.Singular.ExtractText() ?? string.Empty;
                if (name.Length == 0) continue;
                var questRow = npc.QuestRequired.RowId;
                var unlock = questRow >= Quest.QuestCatalog.RowIdBase ? (ushort)(questRow - Quest.QuestCatalog.RowIdBase) : (ushort)0;
                var questLevel = unlock == 0 ? (ushort)0 : quests.GetRowOrDefault(questRow)?.ClassJobLevel[0] ?? 0;
                var level = npc.Level.ValueNullable;
                _clients.Add(new DeliveryClient(npc.RowId, Capitalise(name), npc.DeliveriesPerWeek, unlock, questLevel,
                    level?.Territory.RowId ?? 0,
                    npc.Npc.RowId,
                    level is { } l ? new System.Numerics.Vector3(l.X, l.Y, l.Z) : default));
            }
        }
        catch (Exception ex)
        {
            log($"Delivery catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test constructor.</summary>
    public DeliveryCatalog(IEnumerable<DeliveryClient> clients) => _clients.AddRange(clients);

    public IReadOnlyList<DeliveryClient> All => _clients;

    public DeliveryClient? ByIndex(uint index) => _clients.FirstOrDefault(c => c.Index == index);

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// The real reader: unlock is "the required quest is complete" (the same test vsatisfy uses);
/// rank and used-this-week come from <c>SatisfactionSupplyManager</c>.
/// </summary>
public sealed unsafe class DeliveryState : IDeliveryState
{
    private readonly Quest.IQuestStateReader _quests;
    private readonly IDataManager _data;
    private readonly Action<string>? _log;

    public DeliveryState(Quest.IQuestStateReader quests, IDataManager data, Action<string>? log = null)
    {
        _quests = quests;
        _data = data;
        _log = log;
    }

    public bool IsUnlocked(DeliveryClient client) => client.UnlockQuestId != 0 && _quests.IsComplete(client.UnlockQuestId);

    /// <summary>
    /// True once any rank is non-zero. A character with a client unlocked always has rank ≥ 1 for
    /// it, so all-zero ranks means the arrays have not been filled rather than "everyone is rank 0".
    /// </summary>
    public bool DataLoaded
    {
        get
        {
            try
            {
                var manager = FFXIVClientStructs.FFXIV.Client.Game.SatisfactionSupplyManager.Instance();
                if (manager == null) return false;
                foreach (var rank in manager->SatisfactionRanks)
                    if (rank > 0)
                        return true;
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    public int Rank(DeliveryClient client)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.SatisfactionSupplyManager.Instance();
            if (manager == null) return 0;
            var index = (int)client.Index - 1;
            var ranks = manager->SatisfactionRanks;
            return index >= 0 && index < ranks.Length ? ranks[index] : 0;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Delivery rank read failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Current gauge from the manager, the rank's requirement from
    /// <c>SatisfactionNpc.SatisfactionNpcParams[rank].SatisfactionRequired</c>.
    /// </summary>
    public (int Current, int Max) Satisfaction(DeliveryClient client)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.SatisfactionSupplyManager.Instance();
            if (manager == null) return (0, 0);
            var index = (int)client.Index - 1;
            var gauge = manager->Satisfaction;
            if (index < 0 || index >= gauge.Length) return (0, 0);

            var rank = Rank(client);
            var npc = _data.GetExcelSheet<SatisfactionNpc>().GetRowOrDefault(client.Index);
            if (npc is not { } n) return (gauge[index], 0);
            var parms = n.SatisfactionNpcParams;
            if (rank < 0 || rank >= parms.Count) return (gauge[index], 0);
            return (gauge[index], parms[rank].SatisfactionRequired);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Satisfaction read failed: {ex.Message}");
            return (0, 0);
        }
    }

    public int? UsedThisWeek(DeliveryClient client)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.SatisfactionSupplyManager.Instance();
            if (manager == null) return null;
            var index = (int)client.Index - 1;
            var used = manager->UsedAllowances;
            return index >= 0 && index < used.Length ? used[index] : null;
        }
        catch
        {
            return null;
        }
    }
}
