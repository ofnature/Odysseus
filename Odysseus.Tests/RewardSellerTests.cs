using Odysseus.Services.Quest;

namespace Odysseus.Tests;

/// <summary>
/// The seller's job is to turn "three of these are owed" into vendor sales without ever taking a
/// fourth. Sell takes a whole stack, so the splitting is where that can go wrong.
/// </summary>
public class RewardSellerTests
{
    private const uint Venture = 21072;
    private const uint Gorget = 4305;

    private sealed class Fake : ISellWorld
    {
        public DateTime UtcNow { get; set; } = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        public bool ShopOpen { get; set; } = true;
        /// <summary>Bag contents as stacks; the count is their sum.</summary>
        public Dictionary<uint, List<int>> Stacked { get; } = new();
        public List<string> Calls { get; } = [];
        public bool SplitWorks { get; set; } = true;
        public bool MenuOpens { get; set; } = true;
        public InventoryMenu.ClickResult Click { get; set; } = InventoryMenu.ClickResult.Clicked;
        /// <summary>A click that "sold" removes the stack it was pointed at.</summary>
        public bool SaleLands { get; set; } = true;

        private BagStack? _open;

        public int Held(uint itemId) => Stacked.TryGetValue(itemId, out var s) ? s.Sum() : 0;

        public IReadOnlyList<BagStack> Stacks(uint itemId)
            => Stacked.TryGetValue(itemId, out var s)
                ? s.Select((q, i) => new BagStack(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1, i, q)).ToList()
                : [];

        /// <summary>Which item a slot belongs to; the fake only ever holds one item per test that matters.</summary>
        private uint ItemOf(BagStack stack)
            => Stacked.First(kv => kv.Value.Count > stack.Slot).Key;

        public bool Split(BagStack stack, int quantity)
        {
            Calls.Add($"Split {quantity}");
            if (!SplitWorks) return false;
            var item = ItemOf(stack);
            var list = Stacked[item];
            list[stack.Slot] -= quantity;
            list.Add(quantity);
            return true;
        }

        public bool OpenMenu(BagStack stack)
        {
            Calls.Add($"Menu slot {stack.Slot} x{stack.Quantity}");
            if (!MenuOpens) return false;
            _open = stack;
            return true;
        }

        public InventoryMenu.ClickResult ClickSell()
        {
            Calls.Add("Sell");
            if (Click == InventoryMenu.ClickResult.Clicked && SaleLands && _open is { } stack)
            {
                var item = ItemOf(stack);
                Stacked[item][stack.Slot] = 0;
                Stacked[item].RemoveAll(q => q == 0);
            }
            return Click;
        }

        public void Log(string message) => Calls.Add("Log " + message);

        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    private static void Run(RewardSeller seller, Fake world, int ticks = 40)
    {
        for (var i = 0; i < ticks; i++) { seller.Tick(); world.Advance(0.4); }
    }

    [Fact]
    public void A_stack_that_is_exactly_what_is_owed_sells_whole()
    {
        var world = new Fake();
        world.Stacked[Gorget] = [3];
        var ledger = new RewardLedger([new PendingSale(Gorget, 3)]);
        Run(new RewardSeller(world, ledger, () => true, () => { }), world);

        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Split"));
        Assert.Contains("Sell", world.Calls);
        Assert.False(ledger.Any);
        Assert.Equal(0, world.Held(Gorget));
    }

    /// <summary>
    /// The whole reason splitting exists. One Venture was the reward; two hundred are yours. Sell
    /// takes a whole stack, so the one has to be split off before the menu is ever opened.
    /// </summary>
    [Fact]
    public void One_owed_out_of_a_large_stack_is_split_off_and_the_rest_left_alone()
    {
        var world = new Fake();
        world.Stacked[Venture] = [200];
        var ledger = new RewardLedger([new PendingSale(Venture, 1)]);
        Run(new RewardSeller(world, ledger, () => true, () => { }), world);

        Assert.Contains("Split 1", world.Calls);
        Assert.Contains(world.Calls, c => c.StartsWith("Menu ") && c.EndsWith("x1"));
        Assert.Equal(199, world.Held(Venture));
        Assert.False(ledger.Any);
    }

    [Fact]
    public void Nothing_happens_while_the_toggle_is_off()
    {
        var world = new Fake();
        world.Stacked[Gorget] = [3];
        var ledger = new RewardLedger([new PendingSale(Gorget, 3)]);
        Run(new RewardSeller(world, ledger, () => false, () => { }), world);

        Assert.Empty(world.Calls);
        Assert.True(ledger.Any);
    }

    /// <summary>The vendor window is the session; without one open there is nothing to sell to.</summary>
    [Fact]
    public void Nothing_happens_without_a_vendor_open()
    {
        var world = new Fake { ShopOpen = false };
        world.Stacked[Gorget] = [3];
        var ledger = new RewardLedger([new PendingSale(Gorget, 3)]);
        Run(new RewardSeller(world, ledger, () => true, () => { }), world);

        Assert.Empty(world.Calls);
        Assert.True(ledger.Any);
    }

    /// <summary>
    /// A sale is only banked when the bag actually drops. A click that reports success and changes
    /// nothing must not clear the balance, or the reward is lost without being sold.
    /// </summary>
    [Fact]
    public void A_sale_that_does_not_land_is_not_banked()
    {
        var world = new Fake { SaleLands = false };
        world.Stacked[Gorget] = [3];
        var ledger = new RewardLedger([new PendingSale(Gorget, 3)]);
        Run(new RewardSeller(world, ledger, () => true, () => { }), world);

        Assert.Equal(3, world.Held(Gorget));
        Assert.True(ledger.Any);
    }

    /// <summary>An item the vendor will not take must not stall the ones behind it.</summary>
    [Fact]
    public void An_item_with_no_Sell_entry_is_given_up_on_and_the_next_one_proceeds()
    {
        var world = new Fake { Click = InventoryMenu.ClickResult.EntryMissing };
        world.Stacked[Gorget] = [1];
        var ledger = new RewardLedger([new PendingSale(Gorget, 1)]);
        var seller = new RewardSeller(world, ledger, () => true, () => { });
        Run(seller, world);

        Assert.Contains(world.Calls, c => c.Contains("unsold"));
        Assert.True(ledger.Any);          // the balance is kept; only this session gave up
        Assert.False(seller.Busy);
    }

    /// <summary>Banked but no longer held — used, traded or already gone. Clear it, do not hunt for it.</summary>
    [Fact]
    public void A_balance_for_something_no_longer_in_the_bag_is_cleared_without_selling()
    {
        var world = new Fake();
        var ledger = new RewardLedger([new PendingSale(Gorget, 2)]);
        Run(new RewardSeller(world, ledger, () => true, () => { }), world);

        Assert.DoesNotContain("Sell", world.Calls);
        Assert.False(ledger.Any);
    }

    [Fact]
    public void Losing_the_vendor_mid_sale_abandons_the_attempt()
    {
        var world = new Fake { MenuOpens = false };
        world.Stacked[Gorget] = [1];
        var ledger = new RewardLedger([new PendingSale(Gorget, 1)]);
        var seller = new RewardSeller(world, ledger, () => true, () => { });

        seller.Tick();
        world.Advance(0.4);
        seller.Tick();
        world.ShopOpen = false;
        world.Advance(0.4);
        seller.Tick();

        Assert.False(seller.Busy);
        Assert.True(ledger.Any);
    }
}
