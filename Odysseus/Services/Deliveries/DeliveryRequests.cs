using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>What a client is asking for on one route this week.</summary>
/// <param name="Subrow">Index into the client's <c>SatisfactionSupply</c> subrows.</param>
/// <param name="CollectabilityHigh">The rating that pays the high reward — what the estimate assumes.</param>
/// <param name="CollectabilityLow">
/// The rating the client will accept at all. An item below this cannot be handed over, so it is
/// what counting the bag has to test against — counting by item id alone counts rejects.
/// </param>
public sealed record DeliveryRequest(
    DeliveryRoute Route,
    int Subrow,
    uint ItemId,
    string ItemName,
    ushort CollectabilityHigh,
    ushort CollectabilityLow,
    bool IsBonus);

/// <summary>Works out which items a client wants, before you have walked to them.</summary>
public interface IDeliveryRequests
{
    /// <summary>The three routes' requests, or an empty list when the seed is not readable yet.</summary>
    IReadOnlyList<DeliveryRequest> For(DeliveryClient client, int rank);

    /// <summary>
    /// Every item this client could ask for on a route, whatever the week rolls. Needed because
    /// GatherBuddy takes no request — seeding its list with the whole set once makes the handoff
    /// work every week regardless of the roll.
    /// </summary>
    IReadOnlyList<DeliveryRequest> Possible(DeliveryClient client, int rank, DeliveryRoute route);
}

/// <summary>
/// The weekly requests, derived rather than read.
///
/// <para>
/// The server does not send a list of wanted items; it sends one <c>SupplySeed</c>, and the client
/// rolls the request for each route from it. The roll is a plain xorshift128 (shifts 11/19/8) seeded
/// by mixing the client's supply index with the seed, and each draw picks a
/// <c>SatisfactionSupply</c> subrow for that slot weighted by <c>ProbabilityPercent</c>. Mirrors
/// the game's own <c>SatisfactionSupplyManager.onSatisfactionSupplyRead</c>.
/// </para>
///
/// <para>
/// Deriving it is what lets the runner know what to craft while still standing at the market board.
/// Reading it out of the supply window would mean walking to the client first and finding out too
/// late. The seed only arrives once the client has fetched delivery data, so
/// <see cref="For"/> returns nothing until then — see <see cref="IDeliveryState.DataLoaded"/>.
/// </para>
/// </summary>
public sealed unsafe class DeliveryRequests : IDeliveryRequests
{
    private readonly IDataManager _data;
    private readonly Action<string>? _log;

    public DeliveryRequests(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public IReadOnlyList<DeliveryRequest> For(DeliveryClient client, int rank)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.SatisfactionSupplyManager.Instance();
            if (manager == null) return [];
            var seed = manager->SupplySeed;
            if (seed == 0) return [];

            var npc = _data.GetExcelSheet<SatisfactionNpc>().GetRowOrDefault(client.Index);
            if (npc is not { } n) return [];
            var parms = n.SatisfactionNpcParams;
            if (rank < 0 || rank >= parms.Count) return [];

            return Roll((uint)parms[rank].SupplyIndex, seed);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Delivery request roll failed for {client.Name}: {ex.Message}");
            return [];
        }
    }

    public IReadOnlyList<DeliveryRequest> Possible(DeliveryClient client, int rank, DeliveryRoute route)
    {
        try
        {
            var npc = _data.GetExcelSheet<SatisfactionNpc>().GetRowOrDefault(client.Index);
            if (npc is not { } n) return [];
            var parms = n.SatisfactionNpcParams;
            if (rank < 0 || rank >= parms.Count) return [];

            var subrows = _data.GetSubrowExcelSheet<SatisfactionSupply>().GetRowOrDefault((uint)parms[rank].SupplyIndex);
            if (subrows is not { } rows) return [];

            var all = new List<DeliveryRequest>();
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (row.Slot != (byte)route || row.Item.RowId == 0) continue;
                if (all.Any(x => x.ItemId == row.Item.RowId)) continue;   // the bonus subrow repeats the item
                all.Add(new DeliveryRequest(route, i, row.Item.RowId,
                    row.Item.ValueNullable?.Name.ExtractText() ?? $"item {row.Item.RowId}",
                    row.CollectabilityHigh, row.CollectabilityLow, row.IsBonus));
            }
            return all;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Possible requests for {client.Name} failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>The roll itself, pure so it can be checked against a known seed without the game.</summary>
    public IReadOnlyList<DeliveryRequest> Roll(uint supplyIndex, uint seed)
    {
        var subrows = _data.GetSubrowExcelSheet<SatisfactionSupply>().GetRowOrDefault(supplyIndex);
        if (subrows is not { } rows) return [];

        var rng = new XorShift128(
            (0x03CEA65Cu * supplyIndex) ^ (0x1A0DD20Eu * seed),
            (0xDF585D5Du * supplyIndex) ^ (0x3057656Eu * seed),
            (0xED69E442u * supplyIndex) ^ (0x2202EA5Au * seed),
            (0xAEFC3901u * supplyIndex) ^ (0xE70723F6u * seed));

        var picked = new List<DeliveryRequest>(3);
        for (var slot = 1; slot <= 3; ++slot)
        {
            var weight = 0;
            for (var i = 0; i < rows.Count; ++i)
                if (rows[i].Slot == slot)
                    weight += rows[i].ProbabilityPercent;

            // Draw for every slot even when nothing is eligible: the generator must advance in
            // step with the game's, or the later slots come out wrong.
            var roll = rng.Next();
            if (weight <= 0) continue;
            roll %= (uint)weight;

            for (var i = 0; i < rows.Count; ++i)
            {
                var row = rows[i];
                if (row.Slot != slot) continue;
                if (roll < row.ProbabilityPercent)
                {
                    picked.Add(new DeliveryRequest(
                        (DeliveryRoute)slot, i,
                        row.Item.RowId,
                        row.Item.ValueNullable?.Name.ExtractText() ?? $"item {row.Item.RowId}",
                        row.CollectabilityHigh,
                        row.CollectabilityLow,
                        row.IsBonus));
                    break;
                }
                roll -= row.ProbabilityPercent;
            }
        }
        return picked;
    }

    /// <summary>Textbook xorshift128; the game uses the 11/19/8 triple.</summary>
    private struct XorShift128(uint x, uint y, uint z, uint w)
    {
        public uint Next()
        {
            var t = x ^ (x << 11);
            x = y;
            y = z;
            z = w;
            return w = w ^ (w >> 19) ^ t ^ (t >> 8);
        }
    }
}
