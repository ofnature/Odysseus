using Odysseus.Services.Deliveries;

namespace Odysseus.Tests;

public class ScripLedgerTests
{
    private static readonly ScripKind PurpleCrafters = new(2, 33913, "Purple Crafters' Scrip", 4000);
    private static readonly ScripKind OrangeCrafters = new(6, 41784, "Orange Crafters' Scrip", 4000);

    private static readonly DeliveryClient Zhloe = new(1, "Zhloe Aliapoh", 6, 1551, 60, 478);
    private static readonly DeliveryClient Naago = new(2, "M'naago", 6, 3005, 60, 635);

    private sealed class Currency : ICurrencyReader
    {
        public Dictionary<uint, int> Amounts { get; } = new();
        public int Count(uint itemId) => Amounts.GetValueOrDefault(itemId);
    }

    private sealed class State : IDeliveryState
    {
        public HashSet<uint> Unlocked { get; } = [];
        public Dictionary<uint, int> Used { get; } = new();
        public bool IsUnlocked(DeliveryClient c) => Unlocked.Contains(c.Index);
        public int? UsedThisWeek(DeliveryClient c) => Used.GetValueOrDefault(c.Index);
        public int Rank(DeliveryClient c) => 1;
    }

    private sealed class Rewards : IDeliveryRewards
    {
        public int Normal { get; set; } = 100;
        public int Bonus { get; set; } = 150;
        public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient client, int rank, bool bonus = false)
            => new Dictionary<int, int> { [2] = bonus ? Bonus : Normal, [6] = 60 };
    }

    private sealed class Bonus : IDeliveryBonus
    {
        public HashSet<uint> CraftBonus { get; } = [];
        public int WeekRow => 3;
        public BonusFlags For(DeliveryClient c) => new(CraftBonus.Contains(c.Index), false, false);
    }

    private static (ScripLedger ledger, Currency cur, State state, Bonus bonus, Rewards rewards) Make()
    {
        var cur = new Currency();
        var state = new State();
        var bonus = new Bonus();
        var rewards = new Rewards();
        var catalog = new DeliveryCatalog([Zhloe, Naago]);
        return (new ScripLedger([PurpleCrafters, OrangeCrafters], cur, catalog, state, rewards, bonus), cur, state, bonus, rewards);
    }

    [Fact]
    public void Max_gain_counts_only_unlocked_clients_and_their_remaining_deliveries()
    {
        var (ledger, cur, state, _, _) = Make();
        state.Unlocked.Add(Zhloe.Index);          // 6 left × 100
        state.Unlocked.Add(Naago.Index);
        state.Used[Naago.Index] = 4;              // 2 left × 100
        cur.Amounts[PurpleCrafters.ItemId] = 500;

        var purple = ledger.Read().Single(s => s.Scrip.RewardCurrency == 2);
        Assert.Equal(800, purple.MaxGain);
        Assert.Equal(500, purple.Current);
        Assert.False(purple.WouldOvercap);
        Assert.Equal(3500, purple.Headroom);
    }

    [Fact]
    public void A_bonus_week_raises_the_estimate_for_that_client()
    {
        var (ledger, _, state, bonus, _) = Make();
        state.Unlocked.Add(Zhloe.Index);
        Assert.Equal(600, ledger.Read().Single(s => s.Scrip.RewardCurrency == 2).MaxGain);

        bonus.CraftBonus.Add(Zhloe.Index);        // 6 × 150 instead of 6 × 100
        Assert.Equal(900, ledger.Read().Single(s => s.Scrip.RewardCurrency == 2).MaxGain);
    }

    [Fact]
    public void Overcap_is_what_would_be_thrown_away()
    {
        var (ledger, cur, state, _, _) = Make();
        state.Unlocked.Add(Zhloe.Index);          // 600 incoming
        cur.Amounts[PurpleCrafters.ItemId] = 3800;

        var purple = ledger.Read().Single(s => s.Scrip.RewardCurrency == 2);
        Assert.True(purple.WouldOvercap);
        Assert.Equal(400, purple.Overcap);        // 3800 + 600 - 4000
        Assert.Equal(PurpleCrafters.Name, ledger.WouldOvercap().Single().Scrip.Name);
    }

    [Fact]
    public void A_turn_in_that_would_pass_the_cap_is_refused_with_the_numbers_in_the_reason()
    {
        var (ledger, cur, state, _, _) = Make();
        state.Unlocked.Add(Zhloe.Index);
        cur.Amounts[PurpleCrafters.ItemId] = 3950; // + 100 would spill 50

        var (allowed, reason) = ledger.MayTurnIn(Zhloe);
        Assert.False(allowed);
        Assert.Contains("Purple Crafters' Scrip", reason);
        Assert.Contains("3,950", reason);
        Assert.Contains("50 would be lost", reason);

        cur.Amounts[PurpleCrafters.ItemId] = 3899; // + 100 fits exactly
        Assert.True(ledger.MayTurnIn(Zhloe).Allowed);
    }

    [Fact]
    public void A_locked_or_finished_client_contributes_nothing()
    {
        var (ledger, _, state, _, _) = Make();
        Assert.Equal(0, ledger.RemainingDeliveries(Zhloe));      // locked
        state.Unlocked.Add(Zhloe.Index);
        state.Used[Zhloe.Index] = 6;                              // done this week
        Assert.Equal(0, ledger.RemainingDeliveries(Zhloe));
        Assert.All(ledger.Read(), s => Assert.Equal(0, s.MaxGain));
    }
}
