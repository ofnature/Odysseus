using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>What one delivery pays, by reward-currency index.</summary>
public interface IDeliveryRewards
{
    /// <param name="bonus">This week's bonus applies to the route — the payout comes from the bonus row instead.</param>
    IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient client, int rank, bool bonus = false);

    /// <summary>
    /// How much the satisfaction gauge moves per delivery, from the same reward row. Needed to work
    /// out how many turn-ins are left before the client ranks up and the payout changes.
    /// </summary>
    int SatisfactionPerDelivery(DeliveryClient client, int rank, bool bonus = false);
}

/// <summary>
/// Reads a delivery's scrip payout from the sheets.
///
/// <para>
/// <c>SatisfactionNpc.SatisfactionNpcParams[rank].SupplyIndex</c> → the <c>SatisfactionSupply</c>
/// subrows for that rank → <c>Reward</c> → <c>SatisfactionSupplyRewardData</c>, a pair of
/// <c>{RewardCurrency, QuantityLow/Mid/High}</c>. The <b>high</b> quantity is used: it is what a
/// full-collectability turn-in pays, so an overcap warning built on it is never optimistic.
/// </para>
///
/// <para>
/// Slot 1 is the craft turn-in (2 gather, 3 fish) — the same assumption vsatisfy makes. Only the
/// craft slot is read, because craft is the only route Odysseus can run.
/// </para>
/// </summary>
public sealed class DeliveryRewards : IDeliveryRewards
{
    /// <summary>Supply subrow slot for the crafted item.</summary>
    private const byte CraftSlot = 1;

    /// <summary>Everything one delivery is worth: scrip by currency index, and gauge movement.</summary>
    private sealed record Payout(IReadOnlyDictionary<int, int> Currency, int Satisfaction);

    private readonly IDataManager _data;
    private readonly Action<string>? _log;
    private readonly Dictionary<(uint, int, bool), Payout> _cache = new();

    public DeliveryRewards(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient client, int rank, bool bonus = false)
        => Read(client, rank, bonus).Currency;

    public int SatisfactionPerDelivery(DeliveryClient client, int rank, bool bonus = false)
        => Read(client, rank, bonus).Satisfaction;

    private Payout Read(DeliveryClient client, int rank, bool bonus)
    {
        var key = (client.Index, rank, bonus);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var result = new Dictionary<int, int>();
        var satisfaction = 0;
        try
        {
            var npc = _data.GetExcelSheet<SatisfactionNpc>().GetRowOrDefault(client.Index);
            if (npc is { } n)
            {
                var parms = n.SatisfactionNpcParams;
                var index = Math.Clamp(rank, 0, parms.Count - 1);
                var supplyIndex = (uint)parms[index].SupplyIndex;
                var supply = _data.GetSubrowExcelSheet<SatisfactionSupply>().GetRowOrDefault(supplyIndex);
                if (supply is { } rows)
                {
                    // Each slot has ordinary subrows and one IsBonus subrow that pays from a
                    // larger reward row; take whichever matches this week.
                    foreach (var sub in rows)
                    {
                        if (sub.Slot != CraftSlot || sub.IsBonus != bonus || sub.Reward.ValueNullable is not { } reward) continue;
                        foreach (var entry in reward.SatisfactionSupplyRewardData)
                            if (entry.RewardCurrency != 0 && entry.QuantityHigh > 0)
                                result[entry.RewardCurrency] = entry.QuantityHigh;
                        satisfaction = reward.SatisfactionHigh;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Delivery reward read failed for {client.Name}: {ex.Message}");
        }

        var payout = new Payout(result, satisfaction);
        _cache[key] = payout;
        return payout;
    }
}

/// <summary>Reads currency counts straight out of the character's inventory.</summary>
public sealed unsafe class InventoryCurrencyReader : ICurrencyReader
{
    public int Count(uint itemId)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            return manager == null ? 0 : manager->GetInventoryItemCount(itemId);
        }
        catch
        {
            return 0;
        }
    }
}
