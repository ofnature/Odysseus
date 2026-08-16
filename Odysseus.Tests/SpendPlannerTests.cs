using Odysseus.Services.Deliveries;

namespace Odysseus.Tests;

public class SpendPlannerTests
{
    private static readonly ScripKind Purple = new(2, 33913, "Purple Crafters' Scrip", 4000);
    private static readonly ScripKind Orange = new(6, 41784, "Orange Crafters' Scrip", 4000);
    private static readonly DeliveryClient Zhloe = new(1, "Zhloe Aliapoh", 6, 1551, 60, 478);

    private static readonly ScripOffer CheapBook = new(100, "Master Carpenter I", 2, 400, IsBook: true);
    private static readonly ScripOffer DearBook = new(101, "Master Culinarian X", 2, 1200, IsBook: true);
    private static readonly ScripOffer OrangeBook = new(102, "Master Alchemist XV", 6, 800, IsBook: true);
    private static readonly ScripOffer Materia = new(200, "Craftsman's Command Materia", 2, 100, IsBook: false);
    private static readonly ScripOffer Clay = new(201, "Alkahest", 2, 250, IsBook: false);

    private sealed class Currency : ICurrencyReader
    {
        public Dictionary<uint, int> Amounts { get; } = new();
        public int Count(uint itemId) => Amounts.GetValueOrDefault(itemId);
    }

    private sealed class State : IDeliveryState
    {
        public bool IsUnlocked(DeliveryClient c) => false;   // no incoming scrip: the purse is what it is
        public int? UsedThisWeek(DeliveryClient c) => 0;
        public int Rank(DeliveryClient c) => 1;
        public bool DataLoaded => true;
        public int WeeklyAllowanceUsed => 0;
        public (int Current, int Max) Satisfaction(DeliveryClient c) => (0, 0);
    }

    private sealed class Rewards : IDeliveryRewards
    {
        public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient c, int rank, bool bonus = false, DeliveryRoute route = DeliveryRoute.Craft)
            => new Dictionary<int, int>();
        public int SatisfactionPerDelivery(DeliveryClient c, int rank, bool bonus = false, DeliveryRoute route = DeliveryRoute.Craft) => 0;
    }

    private sealed class Bonus : IDeliveryBonus
    {
        public int WeekRow => 0;
        public string WeekSource => "test";
        public BonusFlags For(DeliveryClient c) => BonusFlags.None;
    }

    private readonly Currency _currency = new();
    private readonly Dictionary<uint, int> _bag = new();
    private readonly HashSet<uint> _learned = [];

    private SpendPlanner Make(params ScripOffer[] offers)
    {
        var ledger = new ScripLedger([Purple, Orange], _currency, new DeliveryCatalog([Zhloe]),
            new State(), new Rewards(), new Bonus());
        var shop = new ScripShop(offers, id => _learned.Contains(id));
        return new SpendPlanner(shop, ledger, id => _bag.GetValueOrDefault(id));
    }

    private static SpendEntry Want(uint itemId, int keep = 0) => new() { ItemId = itemId, Enabled = true, KeepStocked = keep };

    /// <summary>Books lead because they are the only rule that empties itself.</summary>
    [Fact]
    public void Unread_books_are_bought_cheapest_first()
    {
        _currency.Amounts[Purple.ItemId] = 2000;
        var plan = Make(DearBook, CheapBook, Materia).Plan(buyBooks: true, [], reserve: 0);

        Assert.Equal([CheapBook, DearBook], plan.Lines.Select(l => l.Offer));
        Assert.All(plan.Lines, l => Assert.Equal(1, l.Quantity));   // one copy is all a book is worth
        Assert.Equal(1600, plan.TotalFor(Purple.RewardCurrency));
    }

    [Fact]
    public void A_book_already_read_is_never_bought_again()
    {
        _currency.Amounts[Purple.ItemId] = 4000;
        _learned.Add(CheapBook.ItemId);

        var plan = Make(CheapBook, DearBook).Plan(buyBooks: true, [], reserve: 0);
        Assert.Equal(DearBook, plan.Lines.Single().Offer);

        _learned.Add(DearBook.ItemId);
        var done = Make(CheapBook, DearBook).Plan(buyBooks: true, [], reserve: 0);
        Assert.True(done.IsEmpty);
        Assert.Contains("Every book is already read", done.Summary);
    }

    [Fact]
    public void Each_scrip_is_spent_only_on_what_it_buys()
    {
        _currency.Amounts[Purple.ItemId] = 500;     // enough for the purple book only
        _currency.Amounts[Orange.ItemId] = 900;

        var plan = Make(CheapBook, OrangeBook).Plan(buyBooks: true, [], reserve: 0);
        Assert.Equal(400, plan.TotalFor(Purple.RewardCurrency));
        Assert.Equal(800, plan.TotalFor(Orange.RewardCurrency));
    }

    /// <summary>Spending is meant to make room, not to strip the balance before a turn-in needs it.</summary>
    [Fact]
    public void The_reserve_is_left_untouched()
    {
        _currency.Amounts[Purple.ItemId] = 900;
        Assert.Equal(400, Make(CheapBook).Plan(buyBooks: true, [], reserve: 0).TotalFor(2));
        Assert.True(Make(CheapBook).Plan(buyBooks: true, [], reserve: 600).IsEmpty);
    }

    [Fact]
    public void Listed_items_buy_as_many_as_the_purse_allows()
    {
        _currency.Amounts[Purple.ItemId] = 1000;
        var plan = Make(Materia).Plan(buyBooks: false, [Want(Materia.ItemId)], reserve: 0);

        Assert.Equal(10, plan.Lines.Single().Quantity);
        Assert.Equal(1000, plan.TotalFor(2));
    }

    [Fact]
    public void Keep_stocked_counts_what_is_already_held()
    {
        _currency.Amounts[Purple.ItemId] = 4000;
        _bag[Materia.ItemId] = 8;

        var plan = Make(Materia).Plan(buyBooks: false, [Want(Materia.ItemId, keep: 10)], reserve: 0);
        Assert.Equal(2, plan.Lines.Single().Quantity);

        _bag[Materia.ItemId] = 10;
        Assert.True(Make(Materia).Plan(buyBooks: false, [Want(Materia.ItemId, keep: 10)], reserve: 0).IsEmpty);
    }

    [Fact]
    public void Books_come_before_the_list_and_the_list_keeps_its_order()
    {
        _currency.Amounts[Purple.ItemId] = 800;
        var plan = Make(Materia, Clay, CheapBook)
            .Plan(buyBooks: true, [Want(Clay.ItemId, keep: 1), Want(Materia.ItemId, keep: 5)], reserve: 0);

        Assert.Equal([CheapBook, Clay, Materia], plan.Lines.Select(l => l.Offer));
        Assert.Equal(400 + 250 + 100, plan.TotalFor(2));   // the book, one Alkahest, then one materia
    }

    [Fact]
    public void Nothing_ticked_is_a_reason_not_an_empty_plan()
    {
        _currency.Amounts[Purple.ItemId] = 4000;
        var plan = Make(Materia).Plan(buyBooks: false, [new SpendEntry { ItemId = Materia.ItemId, Enabled = false }], reserve: 0);
        Assert.True(plan.IsEmpty);
        Assert.Contains("Nothing is ticked", plan.Summary);
    }

    [Fact]
    public void An_unreadable_shop_says_so_rather_than_planning_nothing()
    {
        var plan = Make().Plan(buyBooks: true, [Want(Materia.ItemId)], reserve: 0);
        Assert.True(plan.IsEmpty);
        Assert.Contains("No scrip vendor stock", plan.Summary);
    }
}
