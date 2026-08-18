using Odysseus.Services.Quest;

namespace Odysseus.Tests;

/// <summary>
/// The safety property of the reward sweep: it may only ever sell what a quest actually handed
/// over. Everything here is a way of getting that wrong.
/// </summary>
public class RewardLedgerTests
{
    private const uint Gorget = 4305;
    private const uint Venture = 21072;
    private const uint AllaganSilver = 5825;

    private readonly Dictionary<uint, int> _bag = new();
    private int Held(uint itemId) => _bag.GetValueOrDefault(itemId);

    [Fact]
    public void Only_what_arrived_between_the_two_counts_is_banked()
    {
        var ledger = new RewardLedger();
        ledger.Before(611, [Gorget, AllaganSilver], Held);
        _bag[AllaganSilver] = 3;                       // the optional reward that was taken

        var gained = ledger.After(611, Held);
        Assert.Equal([new PendingSale(AllaganSilver, 3)], gained);
        Assert.Equal([new PendingSale(AllaganSilver, 3)], ledger.Pending);
    }

    /// <summary>
    /// The sheet lists four or five optional rewards and exactly one arrives. Marking them all is
    /// the obvious mistake and this is what it would cost: three items sold that were never given.
    /// </summary>
    [Fact]
    public void A_candidate_that_never_arrived_is_never_banked()
    {
        var ledger = new RewardLedger();
        ledger.Before(611, [Gorget, AllaganSilver, Venture], Held);
        _bag[Gorget] = 1;

        ledger.After(611, Held);
        Assert.Equal([new PendingSale(Gorget, 1)], ledger.Pending);
    }

    /// <summary>
    /// The one that would really hurt. Ventures are a quest reward and also something you hold two
    /// hundred of for your retainers; banking the id rather than the increase would offer the lot.
    /// </summary>
    [Fact]
    public void A_stack_you_already_owned_is_not_swept_up_with_the_one_you_were_given()
    {
        _bag[Venture] = 200;
        var ledger = new RewardLedger();
        ledger.Before(1497, [Venture], Held);
        _bag[Venture] = 201;

        ledger.After(1497, Held);
        Assert.Equal([new PendingSale(Venture, 1)], ledger.Pending);
        Assert.Equal(1, ledger.Owed(Venture, held: 201));
    }

    [Fact]
    public void An_unmeasured_quest_banks_nothing()
    {
        _bag[Venture] = 200;
        var ledger = new RewardLedger();

        // No Before — a completion we never saw the start of. Without this guard the whole bag
        // reads as a reward.
        Assert.Empty(ledger.After(1497, Held));
        Assert.False(ledger.Any);
    }

    [Fact]
    public void A_completion_of_a_different_quest_does_not_close_the_open_measurement()
    {
        var ledger = new RewardLedger();
        ledger.Before(611, [Gorget], Held);
        _bag[Gorget] = 1;

        Assert.Empty(ledger.After(612, Held));
        Assert.False(ledger.Any);
    }

    [Fact]
    public void What_is_owed_never_exceeds_what_is_in_the_bag()
    {
        var ledger = new RewardLedger([new PendingSale(Gorget, 3)]);
        Assert.Equal(3, ledger.Owed(Gorget, held: 10));
        Assert.Equal(2, ledger.Owed(Gorget, held: 2));   // two were used, only two can be sold
        Assert.Equal(0, ledger.Owed(Gorget, held: 0));
        Assert.Equal(0, ledger.Owed(Venture, held: 99)); // never banked at all
    }

    [Fact]
    public void Selling_draws_the_balance_down_and_clears_it()
    {
        var ledger = new RewardLedger([new PendingSale(Gorget, 3)]);
        ledger.Sold(Gorget, 1);
        Assert.Equal([new PendingSale(Gorget, 2)], ledger.Pending);

        ledger.Sold(Gorget, 5);   // a whole stack went; the balance still just clears
        Assert.False(ledger.Any);
    }

    /// <summary>The list survives a restart, because the vendor is rarely the next thing you reach.</summary>
    [Fact]
    public void A_restored_balance_carries_over()
    {
        var ledger = new RewardLedger([new PendingSale(Gorget, 2), new PendingSale(Venture, 1)]);
        Assert.Equal(2, ledger.Pending.Count);

        ledger.Forget(Venture);
        Assert.Equal([new PendingSale(Gorget, 2)], ledger.Pending);
    }

    /// <summary>Two quests in a row both add, rather than the second replacing the first.</summary>
    [Fact]
    public void Rewards_accumulate_across_quests()
    {
        var ledger = new RewardLedger();
        ledger.Before(611, [AllaganSilver], Held);
        _bag[AllaganSilver] = 3;
        ledger.After(611, Held);

        ledger.Before(613, [AllaganSilver], Held);
        _bag[AllaganSilver] = 8;
        ledger.After(613, Held);

        Assert.Equal([new PendingSale(AllaganSilver, 8)], ledger.Pending);
    }
}
