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
        public bool DataLoaded => true;
        public int WeeklyAllowanceUsed => Used.Values.Sum();
        /// <summary>Default (0, 0) means "gauge full" — no rank-up cap unless a test asks for one.</summary>
        public Dictionary<uint, (int, int)> Gauge { get; } = new();
        public (int Current, int Max) Satisfaction(DeliveryClient c) => Gauge.GetValueOrDefault(c.Index);
    }

    private sealed class Rewards : IDeliveryRewards
    {
        public int Normal { get; set; } = 100;
        public int Bonus { get; set; } = 150;
        public int Satisfaction { get; set; } = 25;
        public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient client, int rank, bool bonus = false)
            => new Dictionary<int, int> { [2] = bonus ? Bonus : Normal, [6] = 60 };
        public int SatisfactionPerDelivery(DeliveryClient client, int rank, bool bonus = false) => Satisfaction;
    }

    private sealed class Bonus : IDeliveryBonus
    {
        public HashSet<uint> CraftBonus { get; } = [];
        public int WeekRow => 3;
        public string WeekSource => "test";
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

    /// <summary>
    /// Turn-ins fill the satisfaction gauge and the rank-up changes the payout, so the estimate must
    /// stop at the rank boundary rather than project the whole allowance at the current rate. This
    /// is what put our figure 4x above Satisfier's.
    /// </summary>
    [Fact]
    public void The_estimate_stops_at_the_next_rank_up()
    {
        var (ledger, _, state, _, rewards) = Make();
        state.Unlocked.Add(Zhloe.Index);
        rewards.Satisfaction = 25;
        state.Gauge[Zhloe.Index] = (100, 150);    // 50 to go, 25 a delivery → 2 turn-ins, not 6

        Assert.Equal(6, ledger.RemainingDeliveries(Zhloe));   // the allowance is untouched
        Assert.Equal(2, ledger.PayingDeliveries(Zhloe));
        Assert.Equal(200, ledger.Read().Single(s => s.Scrip.RewardCurrency == 2).MaxGain);

        state.Gauge[Zhloe.Index] = (140, 150);    // a part delivery still counts as one
        Assert.Equal(1, ledger.PayingDeliveries(Zhloe));
    }

    [Fact]
    public void A_full_gauge_counts_every_remaining_delivery()
    {
        var (ledger, _, state, _, _) = Make();
        state.Unlocked.Add(Zhloe.Index);
        state.Gauge[Zhloe.Index] = (150, 150);    // nothing left to fill — top rank
        Assert.Equal(6, ledger.PayingDeliveries(Zhloe));
        Assert.Equal(600, ledger.Read().Single(s => s.Scrip.RewardCurrency == 2).MaxGain);
    }

    /// <summary>
    /// The bonus rotation is anchored to a weekly reset, so the row must only change on Tuesdays at
    /// 08:00 UTC. If the anchor constant drifts this test catches it without needing the game.
    /// </summary>
    [Fact]
    public void The_bonus_week_advances_on_the_tuesday_reset()
    {
        static int Row(string utc) => DeliveryBonus.ComputeRow(
            DateTimeOffset.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal).ToUnixTimeSeconds(), 12);

        Assert.Equal(0, Row("2022-07-05T08:00:00Z"));   // the anchor reset itself
        Assert.Equal(0, Row("2022-07-12T07:59:59Z"));   // one second before the next
        Assert.Equal(1, Row("2022-07-12T08:00:00Z"));
        Assert.Equal(0, Row("2022-09-27T08:00:00Z"));   // wraps after twelve weeks
        Assert.Equal(10, Row("2026-08-16T13:31:00Z"));  // matches the live client
    }

    /// <summary>
    /// The twelve allowances are shared, not twelve each. Summing every unlocked client's six was
    /// what put Max gain four times above what the week can actually pay.
    /// </summary>
    [Fact]
    public void The_weekly_allowance_is_shared_across_clients()
    {
        var cur = new Currency();
        var state = new State();
        var rewards = new Rewards();
        var clients = Enumerable.Range(1, 5)
            .Select(i => new DeliveryClient((uint)i, $"Client {i}", 6, 1000, 60, 478)).ToList();
        var ledger = new ScripLedger([PurpleCrafters], cur, new DeliveryCatalog(clients), state, rewards, new Bonus());
        foreach (var c in clients) state.Unlocked.Add(c.Index);

        // Five clients × six is thirty deliveries on paper; the week allows twelve.
        Assert.Equal(12, ledger.WeeklyRemaining);
        Assert.Equal(1200, ledger.Read().Single().MaxGain);

        state.Used[clients[0].Index] = 4;                  // four spent anywhere
        Assert.Equal(8, ledger.WeeklyRemaining);
        Assert.Equal(800, ledger.Read().Single().MaxGain);
        Assert.Equal(2, ledger.RemainingDeliveries(clients[0]));  // its own six, minus four used
        Assert.Equal(6, ledger.RemainingDeliveries(clients[1]));  // untouched, and eight still fit
    }

    [Fact]
    public void A_client_cannot_take_more_than_the_week_has_left()
    {
        var (ledger, _, state, _, _) = Make();
        state.Unlocked.Add(Zhloe.Index);
        state.Unlocked.Add(Naago.Index);
        state.Used[Naago.Index] = 6;
        state.Used[Zhloe.Index] = 4;                       // ten of twelve gone

        Assert.Equal(2, ledger.WeeklyRemaining);
        Assert.Equal(2, ledger.RemainingDeliveries(Zhloe)); // its own two, and the week's two agree
        Assert.Equal(0, ledger.RemainingDeliveries(Naago));

        state.Used[Zhloe.Index] = 6;                        // weekly limit hit
        Assert.Equal(0, ledger.WeeklyRemaining);
        Assert.Equal(0, ledger.RemainingDeliveries(Zhloe));
        Assert.All(ledger.Read(), s => Assert.Equal(0, s.MaxGain));
    }

    /// <summary>With a shared budget the estimate has to spend it on the best payers, not the first.</summary>
    [Fact]
    public void Max_gain_spends_the_allowance_on_the_best_paying_clients()
    {
        var cur = new Currency();
        var state = new State();
        var rich = new DeliveryClient(1, "Rich", 6, 1000, 60, 478);
        var poor = new DeliveryClient(2, "Poor", 6, 1000, 60, 478);
        var rewards = new PerClientRewards { Rates = { [1] = 300, [2] = 50 } };
        var ledger = new ScripLedger([PurpleCrafters], cur, new DeliveryCatalog([poor, rich]), state, rewards, new Bonus());
        state.Unlocked.Add(rich.Index);
        state.Unlocked.Add(poor.Index);

        // Twelve to spend: six at 300 from Rich, then six at 50 from Poor.
        Assert.Equal(6 * 300 + 6 * 50, ledger.Read().Single().MaxGain);
    }

    private sealed class PerClientRewards : IDeliveryRewards
    {
        public Dictionary<uint, int> Rates { get; } = new();
        public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient client, int rank, bool bonus = false)
            => new Dictionary<int, int> { [2] = Rates.GetValueOrDefault(client.Index) };
        public int SatisfactionPerDelivery(DeliveryClient client, int rank, bool bonus = false) => 0;
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
