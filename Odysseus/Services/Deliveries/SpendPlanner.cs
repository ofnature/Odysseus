using System;
using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Deliveries;

/// <summary>One line of a spend plan.</summary>
public sealed record SpendLine(ScripOffer Offer, int Quantity)
{
    public int Total => Offer.Cost * Quantity;
}

/// <summary>What spending would do, before any of it happens.</summary>
public sealed record SpendPlan(IReadOnlyList<SpendLine> Lines, string Summary)
{
    public bool IsEmpty => Lines.Count == 0;
    public int TotalFor(int currency) => Lines.Where(l => l.Offer.Currency == currency).Sum(l => l.Total);
    public static SpendPlan Nothing(string why) => new([], why);
}

/// <summary>An item the player has opted into buying with scrips.</summary>
public sealed class SpendEntry
{
    public uint ItemId { get; set; }
    public bool Enabled { get; set; }
    /// <summary>Stop buying once this many are held. 0 means no ceiling.</summary>
    public int KeepStocked { get; set; }
}

/// <summary>
/// Decides what to spend scrips on, without spending any.
///
/// <para>
/// Books first, then the opted-in list in the order it was written. Books lead because they are the
/// only purchase that runs out: once the tomes are read the rule stops firing by itself, and until
/// then they are worth more than anything the filler list holds.
/// </para>
///
/// <para>
/// Nothing here is bought automatically unless the player asked for it. The plan is a proposal, and
/// the same plan drives the manual button and the auto trigger, so what happens unattended is
/// exactly what the button would have shown.
/// </para>
/// </summary>
public sealed class SpendPlanner
{
    private readonly IScripShop _shop;
    private readonly ScripLedger _scrips;
    private readonly Func<uint, int> _held;

    public SpendPlanner(IScripShop shop, ScripLedger scrips, Func<uint, int> held)
    {
        _shop = shop;
        _scrips = scrips;
        _held = held;
    }

    /// <summary>
    /// What to buy right now.
    /// </summary>
    /// <param name="buyBooks">Buy master and folklore books not yet learned.</param>
    /// <param name="list">Opted-in items, in preference order.</param>
    /// <param name="reserve">
    /// Scrips to leave untouched per currency. Spending is meant to make room, not to strip the
    /// balance — a reserve stops an auto-spend from emptying it before a turn-in that needed it.
    /// </param>
    public SpendPlan Plan(bool buyBooks, IReadOnlyList<SpendEntry> list, int reserve = 0)
    {
        if (_shop.Offers.Count == 0)
            return SpendPlan.Nothing("No scrip vendor stock could be read.");
        if (!buyBooks && list.All(e => !e.Enabled))
            return SpendPlan.Nothing("Nothing is ticked to buy.");

        // Spendable scrip per currency, after the reserve.
        var purse = _scrips.Read().ToDictionary(
            s => s.Scrip.RewardCurrency,
            s => Math.Max(0, s.Current - reserve));

        var lines = new List<SpendLine>();

        if (buyBooks)
        {
            // Cheapest first: more tomes read per scrip, and a dear one never starves the rest.
            foreach (var offer in _shop.Offers.Where(o => o.IsBook).OrderBy(o => o.Cost))
            {
                if (_shop.IsLearned(offer)) continue;
                if (!purse.TryGetValue(offer.Currency, out var have) || have < offer.Cost) continue;
                lines.Add(new SpendLine(offer, 1));       // a book is only ever worth one
                purse[offer.Currency] = have - offer.Cost;
            }
        }

        foreach (var entry in list.Where(e => e.Enabled))
        {
            var offer = _shop.Offers.FirstOrDefault(o => o.ItemId == entry.ItemId);
            if (offer is null || offer.Cost <= 0) continue;
            if (!purse.TryGetValue(offer.Currency, out var have) || have < offer.Cost) continue;

            var wanted = entry.KeepStocked > 0 ? entry.KeepStocked - _held(offer.ItemId) : int.MaxValue;
            if (wanted <= 0) continue;

            var affordable = have / offer.Cost;
            var quantity = Math.Min(wanted, affordable);
            if (quantity <= 0) continue;

            lines.Add(new SpendLine(offer, quantity));
            purse[offer.Currency] = have - quantity * offer.Cost;
        }

        if (lines.Count == 0)
            return SpendPlan.Nothing(buyBooks && _shop.Offers.Any(o => o.IsBook) && _shop.Offers.Where(o => o.IsBook).All(_shop.IsLearned)
                ? "Every book is already read, and nothing else is ticked or affordable."
                : "Nothing affordable to buy.");

        var spent = lines.GroupBy(l => l.Offer.Currency)
            .Select(g => $"{g.Sum(l => l.Total):N0} {_scrips.Kinds.FirstOrDefault(k => k.RewardCurrency == g.Key)?.Name ?? g.Key.ToString()}");
        return new SpendPlan(lines, $"{lines.Count} purchase(s) for {string.Join(" and ", spent)}.");
    }

    /// <summary>
    /// The scrips that would overcap, and so are the reason to spend at all. Empty means spending is
    /// optional rather than urgent — which is what the auto trigger keys on.
    /// </summary>
    public IReadOnlyList<ScripStanding> Pressing() => _scrips.WouldOvercap();
}
