using Odysseus.Services.Deliveries;

namespace Odysseus.Tests;

public class SpendRunnerTests
{
    private const uint Shop = 1770631;
    private const uint Vendor = 1027232;
    private static readonly ScripOffer Book = new(100, "Master Carpenter I", 2, 400, IsBook: true, ShopId: Shop);
    private static readonly ScripOffer Materia = new(200, "Command Materia", 2, 100, IsBook: false, ShopId: Shop);
    private static readonly ScripOffer Elsewhere = new(300, "Something Else", 6, 100, IsBook: false, ShopId: 999);

    /// <summary>A scrip window that only hands over what it was actually asked for.</summary>
    private sealed class Game : IDeliveryWorld
    {
        public Dictionary<uint, int> Bag { get; } = new();
        public bool VendorNearby { get; set; } = true;
        public bool ShopOpen { get; set; }
        public bool OpenRefused { get; set; }
        public List<uint> Bought { get; } = [];
        public int Closes { get; private set; }
        /// <summary>Simulates a wrong callback: the click does nothing at all.</summary>
        public bool SilentlyDoesNothing { get; set; }
        /// <summary>Simulates a worse one: it buys, but not the item asked for.</summary>
        public uint BuysInstead { get; set; }
        public HashSet<uint> NotListed { get; } = [];

        public uint FindSpecialShopVendor(uint shopId) => VendorNearby && shopId == Shop ? Vendor : 0;
        public bool IsSpecialShopOpen => ShopOpen;
        public bool OpenSpecialShop(uint vendorDataId, uint shopId) { if (!OpenRefused) ShopOpen = true; return !OpenRefused; }
        public void CloseSpecialShop() { Closes++; ShopOpen = false; }

        public bool BuyOneFromSpecialShop(uint itemId)
        {
            if (NotListed.Contains(itemId)) return false;
            Bought.Add(itemId);
            if (SilentlyDoesNothing) return true;
            var got = BuysInstead != 0 ? BuysInstead : itemId;
            Bag[got] = Bag.GetValueOrDefault(got) + 1;
            return true;
        }

        public int ItemCount(uint itemId, int minCollectability = 0) => Bag.GetValueOrDefault(itemId);

        // Not used by spending.
        public bool IsSupplyOpen(DeliveryClient c) => false;
        public void OpenRoute(DeliveryRoute route) { }
        public bool IsTradeOpen(uint itemId) => false;
        public bool CommitTrade(DeliveryRoute route) => false;
        public int CurrentCraftType => -1;
        public bool IsShopOpen(uint shopId) => false;
        public bool OpenShop(uint vendorDataId, uint shopId) => false;
        public bool BuyFromShop(uint shopId, uint itemId, int count) => false;
        public bool ShopBusy(uint shopId) => false;
        public void CloseShop() { }
        public int Gil => 0;
    }

    private readonly Game _game = new();
    private readonly List<string> _log = [];
    private DateTime _now = new(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
    private readonly SpendRunner _runner;

    public SpendRunnerTests() => _runner = new SpendRunner(_game, _log.Add, () => _now);

    private static SpendPlan Plan(params SpendLine[] lines) => new(lines, $"{lines.Length} purchase(s).");

    private void Run(int frames = 80)
    {
        for (var i = 0; i < frames && !_runner.IsFinished; i++)
        {
            _runner.Tick();
            _now = _now.AddSeconds(2);
        }
    }

    [Fact]
    public void A_plan_is_bought_one_unit_at_a_time()
    {
        Assert.True(_runner.Start(Plan(new SpendLine(Book, 1), new SpendLine(Materia, 3))));
        Run();

        Assert.Equal(SpendRunState.Done, _runner.State);
        Assert.Equal([Book.ItemId, Materia.ItemId, Materia.ItemId, Materia.ItemId], _game.Bought);
        Assert.Equal(4, _runner.Bought);
        Assert.Equal(3, _game.Bag[Materia.ItemId]);
        Assert.True(_game.Closes > 0);
    }

    /// <summary>
    /// The window has no agent to drive, so the callback shape cannot be checked offline. Verifying
    /// the item arrived is what keeps a wrong one from emptying the balance.
    /// </summary>
    [Fact]
    public void A_click_that_buys_nothing_stops_after_one_attempt()
    {
        _game.SilentlyDoesNothing = true;
        Assert.True(_runner.Start(Plan(new SpendLine(Materia, 10))));
        Run();

        Assert.Equal(SpendRunState.Blocked, _runner.State);
        Assert.Single(_game.Bought);                 // tried once, never again
        Assert.Equal(0, _runner.Bought);
        Assert.Contains("did not arrive", _runner.StatusLine);
    }

    [Fact]
    public void A_click_that_buys_the_wrong_item_stops_after_one_purchase()
    {
        _game.BuysInstead = 999;
        Assert.True(_runner.Start(Plan(new SpendLine(Materia, 10))));
        Run();

        Assert.Equal(SpendRunState.Blocked, _runner.State);
        Assert.Single(_game.Bought);
        Assert.Equal(1, _game.Bag[999]);             // one wrong purchase is the whole cost
        Assert.Equal(0, _runner.Bought);
    }

    [Fact]
    public void No_vendor_nearby_refuses_to_start_and_says_it_does_not_travel()
    {
        _game.VendorNearby = false;
        Assert.False(_runner.Start(Plan(new SpendLine(Materia, 1))));
        Assert.Contains("does not travel", _runner.StatusLine);
        Assert.Empty(_game.Bought);
    }

    [Fact]
    public void An_already_open_window_is_used_without_a_vendor_in_range()
    {
        _game.VendorNearby = false;
        _game.ShopOpen = true;
        Assert.True(_runner.Start(Plan(new SpendLine(Materia, 2))));
        Run();

        Assert.Equal(SpendRunState.Done, _runner.State);
        Assert.Equal(2, _runner.Bought);
    }

    [Fact]
    public void A_window_that_never_opens_stops_rather_than_hammering_it()
    {
        _game.OpenRefused = true;
        Assert.True(_runner.Start(Plan(new SpendLine(Materia, 1))));
        Run();

        Assert.Equal(SpendRunState.Blocked, _runner.State);
        Assert.Contains("Could not open", _runner.StatusLine);
        Assert.Empty(_game.Bought);
    }

    [Fact]
    public void An_item_the_vendor_does_not_list_stops_with_its_name()
    {
        _game.NotListed.Add(Materia.ItemId);
        Assert.True(_runner.Start(Plan(new SpendLine(Materia, 1))));
        Run();

        Assert.Equal(SpendRunState.Blocked, _runner.State);
        Assert.Contains("Command Materia", _runner.StatusLine);
        Assert.Contains("not on this vendor's list", _runner.StatusLine);
    }

    /// <summary>One window can only sell its own stock; the rest is left rather than half-attempted.</summary>
    [Fact]
    public void Lines_belonging_to_another_vendor_are_left_behind()
    {
        Assert.True(_runner.Start(Plan(new SpendLine(Materia, 1), new SpendLine(Elsewhere, 1))));
        Run();

        Assert.Equal(SpendRunState.Done, _runner.State);
        Assert.Equal([Materia.ItemId], _game.Bought);
        Assert.Contains(_log, l => l.Contains("different vendor"));
    }

    [Fact]
    public void An_empty_plan_is_refused_with_its_own_reason()
    {
        Assert.False(_runner.Start(SpendPlan.Nothing("Nothing is ticked to buy.")));
        Assert.Contains("Nothing is ticked", _runner.StatusLine);
    }
}
