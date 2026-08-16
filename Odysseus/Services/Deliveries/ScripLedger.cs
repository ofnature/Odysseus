using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>A scrip currency: how the reward data names it, what item it is, and its cap.</summary>
/// <param name="RewardCurrency">
/// The index used by <c>SatisfactionSupplyReward</c>'s reward data — 2 purple crafters',
/// 4 purple gatherers', 6 orange crafters', 7 orange gatherers'. Verified against the live
/// currency window 2026-08-16.
/// </param>
public sealed record ScripKind(int RewardCurrency, uint ItemId, string Name, int Cap);

/// <summary>Where a scrip stands, and what this week's remaining deliveries would do to it.</summary>
public sealed record ScripStanding(ScripKind Scrip, int Current, int MaxGain)
{
    public int Cap => Scrip.Cap;
    public int Headroom => Math.Max(0, Cap - Current);
    /// <summary>How much of <see cref="MaxGain"/> would be thrown away by hitting the cap. 0 when it fits.</summary>
    public int Overcap => Math.Max(0, Current + MaxGain - Cap);
    public bool WouldOvercap => Overcap > 0;
}

/// <summary>Reads currency amounts from the character's inventory.</summary>
public interface ICurrencyReader
{
    int Count(uint itemId);
}

/// <summary>
/// The scrip side of custom deliveries: what you hold, what the remaining deliveries would pay,
/// and whether that would spill over the cap.
///
/// <para>
/// Caps come from the item's stack size (4,000 for every scrip; the sheet is the authority, so a
/// cap change needs no code). Rewards come from <c>SatisfactionSupply → Reward →
/// SatisfactionSupplyRewardData</c>, which pays in reward-currency indices rather than item ids —
/// hence <see cref="ScripKind.RewardCurrency"/>. The estimate uses the <b>high</b> quantity, the
/// most a delivery can pay, so a warning is never an under-estimate.
/// </para>
/// </summary>
public sealed class ScripLedger
{
    /// <summary>Current-expansion scrips. Older ones (white, yellow) are not paid by deliveries any more.</summary>
    private static readonly (int Currency, uint Item)[] Known =
    [
        (2, 33913), // Purple Crafters'
        (4, 33914), // Purple Gatherers'
        (6, 41784), // Orange Crafters'
        (7, 41785), // Orange Gatherers'
    ];

    private readonly List<ScripKind> _kinds = [];
    private readonly ICurrencyReader _currency;
    private readonly DeliveryCatalog _clients;
    private readonly IDeliveryState _state;
    private readonly IDeliveryRewards _rewards;
    private readonly IDeliveryBonus _bonus;

    public ScripLedger(IDataManager data, ICurrencyReader currency, DeliveryCatalog clients, IDeliveryState state, IDeliveryRewards rewards, IDeliveryBonus bonus, Action<string> log)
    {
        _currency = currency;
        _clients = clients;
        _state = state;
        _rewards = rewards;
        _bonus = bonus;
        try
        {
            var items = data.GetExcelSheet<Item>();
            foreach (var (cur, item) in Known)
            {
                var row = items.GetRowOrDefault(item);
                if (row is not { } r) continue;
                _kinds.Add(new ScripKind(cur, item, r.Name.ExtractText(), r.StackSize > 0 ? (int)r.StackSize : 4000));
            }
        }
        catch (Exception ex)
        {
            log($"Scrip ledger failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test constructor.</summary>
    public ScripLedger(IEnumerable<ScripKind> kinds, ICurrencyReader currency, DeliveryCatalog clients, IDeliveryState state, IDeliveryRewards rewards, IDeliveryBonus bonus)
    {
        _kinds.AddRange(kinds);
        _currency = currency;
        _clients = clients;
        _state = state;
        _rewards = rewards;
        _bonus = bonus;
    }

    public IReadOnlyList<ScripKind> Kinds => _kinds;

    /// <summary>Every scrip with its current amount and what the week's remaining deliveries could add.</summary>
    public IReadOnlyList<ScripStanding> Read()
    {
        var gain = MaxGainByCurrency();
        return _kinds.Select(k => new ScripStanding(k, _currency.Count(k.ItemId), gain.GetValueOrDefault(k.RewardCurrency))).ToList();
    }

    /// <summary>What one more craft delivery from this client would pay, by reward-currency index.</summary>
    public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient client)
        => _rewards.PerDelivery(client, _state.Rank(client), _bonus.For(client).Craft);

    /// <summary>
    /// Would turning in everything still available this week overflow any scrip? The list is the
    /// scrips that would spill.
    /// </summary>
    public IReadOnlyList<ScripStanding> WouldOvercap() => Read().Where(s => s.WouldOvercap).ToList();

    /// <summary>
    /// Whether one more craft turn-in for this client is allowed, and why not.
    ///
    /// <para>
    /// The rule the runner obeys: a turn-in that would push a scrip past its cap wastes the
    /// overflow, so it stops instead — with the scrip named and the numbers shown.
    /// </para>
    /// </summary>
    public (bool Allowed, string? Reason) MayTurnIn(DeliveryClient client)
    {
        var payout = PerDelivery(client);
        foreach (var standing in Read())
        {
            if (!payout.TryGetValue(standing.Scrip.RewardCurrency, out var amount) || amount <= 0) continue;
            if (standing.Current + amount <= standing.Cap) continue;
            return (false,
                $"{client.Name}'s next turn-in pays {amount:N0} {standing.Scrip.Name}, but you hold " +
                $"{standing.Current:N0} of {standing.Cap:N0} — {standing.Current + amount - standing.Cap:N0} would be lost. " +
                "Spend some scrips, then run it again.");
        }
        return (true, null);
    }

    /// <summary>Deliveries left this week for a client (0 when locked or unreadable).</summary>
    public int RemainingDeliveries(DeliveryClient client)
    {
        if (!_state.IsUnlocked(client)) return 0;
        var used = _state.UsedThisWeek(client);
        return used is { } u ? Math.Max(0, client.DeliveriesPerWeek - u) : client.DeliveriesPerWeek;
    }

    private Dictionary<int, int> MaxGainByCurrency()
    {
        var total = new Dictionary<int, int>();
        foreach (var client in _clients.All)
        {
            var remaining = RemainingDeliveries(client);
            if (remaining <= 0) continue;
            foreach (var (currency, amount) in _rewards.PerDelivery(client, _state.Rank(client), _bonus.For(client).Craft))
                total[currency] = total.GetValueOrDefault(currency) + amount * remaining;
        }
        return total;
    }
}
