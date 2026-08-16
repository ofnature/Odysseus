using System.Numerics;
using Odysseus.Services.Deliveries;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class DeliveryRunnerTests
{
    private const uint ItemId = 40000;

    private static readonly ScripKind Purple = new(2, 33913, "Purple Crafters' Scrip", 4000);
    private static readonly DeliveryClient Zhloe =
        new(1, "Zhloe Aliapoh", 6, 1551, 60, 478, 1017492, new Vector3(10, 0, 10));

    private sealed class Currency : ICurrencyReader
    {
        public Dictionary<uint, int> Amounts { get; } = new();
        public int Count(uint itemId) => Amounts.GetValueOrDefault(itemId);
    }

    private sealed class State : IDeliveryState
    {
        public bool Unlocked { get; set; } = true;
        public int Used { get; set; }
        public bool IsUnlocked(DeliveryClient c) => Unlocked;
        public int? UsedThisWeek(DeliveryClient c) => Used;
        public int Rank(DeliveryClient c) => 1;
        public bool DataLoaded { get; set; } = true;
        public int WeeklyAllowanceUsed { get; set; }
        public (int Current, int Max) Satisfaction(DeliveryClient c) => (0, 0);
    }

    private sealed class Rewards : IDeliveryRewards
    {
        public int PerTurnIn { get; set; } = 100;
        public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient c, int rank, bool bonus = false, DeliveryRoute route = DeliveryRoute.Craft)
            => new Dictionary<int, int> { [2] = PerTurnIn };
        public int SatisfactionPerDelivery(DeliveryClient c, int rank, bool bonus = false, DeliveryRoute route = DeliveryRoute.Craft) => 0;
    }

    private sealed class Bonus : IDeliveryBonus
    {
        public int WeekRow => 0;
        public string WeekSource => "test";
        public BonusFlags For(DeliveryClient c) => BonusFlags.None;
    }

    private const uint GatherItemId = 40001;

    private sealed class Requests : IDeliveryRequests
    {
        public List<DeliveryRequest> Items { get; } =
        [
            new(DeliveryRoute.Craft, 0, ItemId, "Rroneek Cheese", 500, false),
            new(DeliveryRoute.Gather, 1, GatherItemId, "Yak T'el Marlin", 400, false),
        ];
        public IReadOnlyList<DeliveryRequest> For(DeliveryClient c, int rank) => Items;
    }

    private sealed class Gathering : IGatheringSource
    {
        public GatheringOrigin? Origin { get; set; } = new("Botanist", 100, "Ok'hanu", "Yak T'el");
        public GatheringOrigin? For(uint itemId) => Origin;
    }

    private sealed class Crafter : ICrafter
    {
        public bool Available { get; set; } = true;
        public bool IsCrafting { get; set; }
        public List<(ushort Recipe, int Amount)> Asked { get; } = [];
        public bool CraftItem(ushort recipeId, int amount) { Asked.Add((recipeId, amount)); return true; }
        public void StopCrafting() => IsCrafting = false;
    }

    private sealed class Recipes : IRecipeLookup
    {
        /// <summary>The same item, makeable by two jobs — CRP first in the sheet, CUL second.</summary>
        public List<RecipeOption> Options { get; } = [new(4242, 0, 90), new(4343, 7, 90)];
        public IReadOnlyList<RecipeOption> OptionsFor(uint itemId) => Options;
    }

    /// <summary>The supply and trade windows, faked as a two-step handshake per turn-in.</summary>
    private sealed class Game : IDeliveryWorld
    {
        public Dictionary<uint, int> Bag { get; } = new();
        public bool SupplyOpen { get; set; } = true;
        public bool TradeOpen { get; set; }
        public bool RefuseCommit { get; set; }
        public int Committed { get; private set; }
        public int CurrentCraftType { get; set; } = -1;

        /// <summary>Whatever the open trade is asking for — the route decides which item that is.</summary>
        private uint _pending;

        public bool IsSupplyOpen(DeliveryClient c) => SupplyOpen;
        public void OpenRoute(DeliveryRoute route) => TradeOpen = true;
        public bool IsTradeOpen(uint itemId) { if (TradeOpen) _pending = itemId; return TradeOpen; }
        public int ItemCount(uint itemId) => Bag.GetValueOrDefault(itemId);

        public bool CommitTrade(DeliveryRoute route)
        {
            if (RefuseCommit) return false;
            Committed++;
            TradeOpen = false;
            Bag[_pending] = Math.Max(0, Bag.GetValueOrDefault(_pending) - 1);
            return true;
        }

        // ── Vendor ──
        public int Gil { get; set; } = 1_000_000;
        public uint OpenShopId { get; set; }
        public bool ShopStocked { get; set; } = true;
        public List<(uint Item, int Count)> Bought { get; } = [];
        public bool IsShopOpen(uint shopId) => OpenShopId == shopId && shopId != 0;
        public bool OpenShop(uint vendorDataId, uint shopId) { OpenShopId = shopId; return true; }
        public bool ShopBusy(uint shopId) => false;
        public void CloseShop() => OpenShopId = 0;

        public bool BuyFromShop(uint shopId, uint itemId, int count)
        {
            if (!ShopStocked) return false;
            Bought.Add((itemId, count));
            Bag[itemId] = Bag.GetValueOrDefault(itemId) + count;
            Gil -= count * 100;
            return true;
        }
    }

    private const uint IngredientId = 5000;
    private const uint VendorId = 1000001;

    private sealed class Ingredients : IIngredientSource
    {
        public bool HasVendor { get; set; } = true;
        public uint Cost { get; set; } = 100;
        public Func<uint, int>? LastHeld { get; private set; }

        public IReadOnlyList<IngredientNeed> Plan(ushort recipeId, int crafts, Func<uint, int> held)
        {
            LastHeld = held;
            return
            [
                new IngredientNeed(IngredientId, "Rroneek Chuck", 2, 2 * crafts, held(IngredientId),
                    HasVendor ? 262144u : 0u, HasVendor ? VendorId : 0u, "Trader", Cost),
            ];
        }
    }

    private readonly FakeStepWorld _world = new() { ArriveOnMove = true, TerritoryId = 478 };
    private readonly Currency _currency = new();
    private readonly State _state = new();
    private readonly Rewards _rewards = new();
    private readonly Requests _requests = new();
    private readonly Crafter _crafter = new();
    private readonly Recipes _recipes = new();
    private readonly Game _game = new();
    private readonly Ingredients _ingredients = new();
    private readonly Gathering _gathering = new();
    private readonly ScripLedger _scrips;
    private readonly DeliveryRunner _runner;
    private readonly List<string> _log = [];
    private int _preferredJob = -1;

    public DeliveryRunnerTests()
    {
        _scrips = new ScripLedger([Purple], _currency, new DeliveryCatalog([Zhloe]), _state, _rewards, new Bonus());
        _runner = new DeliveryRunner(_world, _game, _state, _requests, _scrips, _crafter, _recipes, _ingredients, _gathering,
            new StepExecutor(_world), () => _preferredJob, _log.Add);
        _world.PlayerPosition = Zhloe.Position;   // standing at the client
        _world.Spawned.Add(Zhloe.NpcDataId);
        _world.Spawned.Add(VendorId);             // ...with the merchant beside them
    }

    /// <summary>Tick until the runner reaches a phase, so tests need not count frames.</summary>
    private void RunTo(DeliveryRunState state, int frames = 40)
    {
        for (var i = 0; i < frames && _runner.State != state && !_runner.IsFinished; i++)
        {
            _runner.Tick();
            _world.UtcNow = _world.UtcNow.AddSeconds(2);
        }
        Assert.Equal(state, _runner.State);
    }

    /// <summary>Run frames until the state settles or the budget runs out.</summary>
    private void Run(int frames = 60)
    {
        for (var i = 0; i < frames; i++)
        {
            if (_runner.IsFinished) return;
            _runner.Tick();
            _world.UtcNow = _world.UtcNow.AddSeconds(2);   // past the per-turn-in gap
        }
    }

    [Fact]
    public void A_full_week_of_deliveries_runs_to_done()
    {
        _game.Bag[ItemId] = 6;
        Assert.True(_runner.Start(Zhloe));
        Run();

        Assert.Equal(DeliveryRunState.Done, _runner.State);
        Assert.Equal(6, _runner.Delivered);
        Assert.Equal(6, _game.Committed);
        Assert.Empty(_crafter.Asked);              // already stocked, so nothing to craft
    }

    /// <summary>The one-shot used to prove a client end to end before trusting it with a week.</summary>
    [Fact]
    public void A_test_run_delivers_once_and_stops()
    {
        _game.Bag[ItemId] = 6;
        Assert.True(_runner.Start(Zhloe, limit: 1));
        Assert.Equal(1, _runner.Target);
        Run();

        Assert.Equal(DeliveryRunState.Done, _runner.State);
        Assert.Equal(1, _game.Committed);
        Assert.Equal(5, _game.Bag[ItemId]);
    }

    [Fact]
    public void A_test_run_crafts_only_the_one()
    {
        _game.Bag[ItemId] = 0;
        Assert.True(_runner.Start(Zhloe, limit: 1));
        RunTo(DeliveryRunState.Craft);
        _runner.Tick();
        Assert.Equal(((ushort)4242, 1), _crafter.Asked.Single());
    }

    [Fact]
    public void A_short_bag_goes_to_artisan_for_exactly_what_is_missing()
    {
        _game.Bag[ItemId] = 2;
        _state.Used = 1;                            // 5 to do, 2 in hand
        _state.WeeklyAllowanceUsed = 1;
        Assert.True(_runner.Start(Zhloe));
        RunTo(DeliveryRunState.Craft);
        _runner.Tick();

        Assert.Equal(((ushort)4242, 3), _crafter.Asked.Single());
    }

    /// <summary>The cap is a hard stop: refuse before the first turn-in, not after.</summary>
    [Fact]
    public void Starting_at_the_cap_is_refused_with_the_reason()
    {
        _currency.Amounts[Purple.ItemId] = 3950;    // +100 would spill 50
        _game.Bag[ItemId] = 6;

        Assert.False(_runner.Start(Zhloe));
        Assert.Contains("50 would be lost", _runner.StatusLine);
        Assert.Equal(DeliveryStop.ScripCap, _runner.StoppedBecause);
        Assert.Equal(0, _game.Committed);
    }

    /// <summary>
    /// Artisan switches the character to the recipe's job, so which recipe is chosen is the whole
    /// question. Staying put beats moving; an explicit preference beats staying put.
    /// </summary>
    [Fact]
    public void The_recipe_chosen_decides_the_job_and_the_current_one_wins_by_default()
    {
        _game.Bag[ItemId] = 0;

        _game.CurrentCraftType = 7;                 // standing there as a Culinarian
        Assert.True(_runner.Start(Zhloe, limit: 1));
        RunTo(DeliveryRunState.Craft);
        _runner.Tick();
        Assert.Equal((ushort)4343, _crafter.Asked.Single().Recipe);

        _crafter.Asked.Clear();
        _preferredJob = 0;                          // configured to Carpenter regardless
        _runner.Stop();
        Assert.True(_runner.Start(Zhloe, limit: 1));
        RunTo(DeliveryRunState.Craft);
        _runner.Tick();
        Assert.Equal((ushort)4242, _crafter.Asked.Single().Recipe);
    }

    [Fact]
    public void A_stopped_run_reports_itself_finished_so_the_button_goes_back()
    {
        _game.Bag[ItemId] = 6;
        Assert.True(_runner.Start(Zhloe));
        Assert.False(_runner.IsFinished);
        Run();
        Assert.True(_runner.IsFinished);

        _crafter.Available = false;
        _game.Bag[ItemId] = 0;
        Assert.True(_runner.Start(Zhloe));
        Run();
        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Equal(DeliveryStop.Materials, _runner.StoppedBecause);
        Assert.True(_runner.IsFinished);
    }

    [Fact]
    public void Reaching_the_cap_mid_run_stops_and_says_why()
    {
        _game.Bag[ItemId] = 6;
        _currency.Amounts[Purple.ItemId] = 3700;    // room for three at 100 each
        Assert.True(_runner.Start(Zhloe));

        // The ledger reads live, so bank each payout as the game would.
        for (var i = 0; i < 40 && _runner.State is not (DeliveryRunState.Blocked or DeliveryRunState.Done); i++)
        {
            var before = _game.Committed;
            _runner.Tick();
            if (_game.Committed > before)
                _currency.Amounts[Purple.ItemId] += 100;
            _world.UtcNow = _world.UtcNow.AddSeconds(2);
        }

        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Equal(3, _game.Committed);
        Assert.Equal(4000, _currency.Amounts[Purple.ItemId]);
        Assert.Contains("Purple Crafters' Scrip", _runner.StatusLine);
    }

    [Fact]
    public void Without_artisan_it_stops_and_names_what_is_missing()
    {
        _crafter.Available = false;
        _game.Bag[ItemId] = 1;
        Assert.True(_runner.Start(Zhloe));
        Run();

        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Contains("5 × Rroneek Cheese", _runner.StatusLine);
        Assert.Contains("Artisan is not installed", _runner.StatusLine);
    }

    [Fact]
    public void Artisan_giving_up_short_reports_the_shortfall_rather_than_looping()
    {
        _game.Bag[ItemId] = 0;
        Assert.True(_runner.Start(Zhloe));
        RunTo(DeliveryRunState.Craft);
        _runner.Tick();                             // asks Artisan for six
        Assert.Single(_crafter.Asked);
        _runner.Tick();                             // Artisan is not running and the bag is still empty

        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Contains("Nothing nearby sells the rest", _runner.StatusLine);
        Assert.Single(_crafter.Asked);              // and it did not ask again
    }

    /// <summary>
    /// Travel comes before shopping so the merchant beside the client is in reach. Two crafts need
    /// four of an ingredient; the bag has one, so three get bought and Artisan is asked afterwards.
    /// </summary>
    [Fact]
    public void Ingredients_are_bought_from_the_merchant_beside_the_client()
    {
        _game.Bag[ItemId] = 4;                      // four made, two still to make
        _game.Bag[IngredientId] = 1;
        _state.WeeklyAllowanceUsed = 6;             // six left for the week, six for the client
        Assert.True(_runner.Start(Zhloe));
        Assert.Equal(6, _runner.Target);

        Run();
        Assert.Equal((IngredientId, 3), _game.Bought.Single());   // 2 crafts × 2 each, minus the one held
        Assert.Equal(((ushort)4242, 2), _crafter.Asked.Single());
    }

    [Fact]
    public void Shopping_is_skipped_when_the_bag_already_has_enough()
    {
        _game.Bag[ItemId] = 6;
        Assert.True(_runner.Start(Zhloe));
        Run();

        Assert.Empty(_game.Bought);
        Assert.Empty(_crafter.Asked);
        Assert.Equal(DeliveryRunState.Done, _runner.State);
    }

    /// <summary>Nothing nearby sells it, so hand it to Artisan and let it say what is missing.</summary>
    [Fact]
    public void An_ingredient_with_no_nearby_vendor_falls_through_to_artisan()
    {
        _ingredients.HasVendor = false;
        _game.Bag[ItemId] = 5;
        Assert.True(_runner.Start(Zhloe, limit: 6));
        Run();

        Assert.Empty(_game.Bought);
        Assert.Equal(((ushort)4242, 1), _crafter.Asked.Single());
    }

    [Fact]
    public void Not_enough_gil_stops_before_buying_anything()
    {
        _game.Bag[ItemId] = 0;
        _game.Gil = 50;                             // 12 ingredients at 100 each is well past it
        Assert.True(_runner.Start(Zhloe, limit: 1));
        Run();

        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Equal(DeliveryStop.Materials, _runner.StoppedBecause);
        Assert.Contains("gil", _runner.StatusLine);
        Assert.Empty(_game.Bought);
    }

    /// <summary>
    /// Gather has no sourcing of its own, but everything after it is shared — so with the items in
    /// the bag the route runs end to end, never touching the vendor or Artisan.
    /// </summary>
    [Fact]
    public void A_gather_delivery_runs_when_the_items_are_already_in_the_bag()
    {
        _game.Bag[GatherItemId] = 6;
        Assert.True(_runner.Start(Zhloe, DeliveryRoute.Gather));
        Run();

        Assert.Equal(DeliveryRunState.Done, _runner.State);
        Assert.Equal(6, _game.Committed);
        Assert.Equal(0, _game.Bag[GatherItemId]);   // the gathered items, not the crafted one
        Assert.Empty(_game.Bought);
        Assert.Empty(_crafter.Asked);
        Assert.Equal(DeliveryRoute.Gather, _runner.Route);
    }

    [Fact]
    public void A_short_gather_bag_stops_and_says_where_to_find_them()
    {
        _game.Bag[GatherItemId] = 2;
        Assert.True(_runner.Start(Zhloe, DeliveryRoute.Gather));
        Run();

        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Equal(DeliveryStop.Materials, _runner.StoppedBecause);
        Assert.Contains("4 × Yak T'el Marlin", _runner.StatusLine);
        Assert.Contains("collectability 400", _runner.StatusLine);
        Assert.Contains("Ok'hanu, Yak T'el — Botanist Lv 100", _runner.StatusLine);
        Assert.Equal(0, _game.Committed);
    }

    [Fact]
    public void An_item_no_node_lists_still_stops_cleanly()
    {
        _gathering.Origin = null;
        Assert.True(_runner.Start(Zhloe, DeliveryRoute.Gather, limit: 1));
        Run();

        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Contains("does not gather yet", _runner.StatusLine);
        Assert.DoesNotContain("Found at", _runner.StatusLine);
    }

    /// <summary>Two different limits, two different reasons — "done this week" is not specific enough.</summary>
    [Fact]
    public void The_weekly_limit_and_the_client_limit_give_different_reasons()
    {
        _state.Used = 6;                            // this client is finished, the week is not
        _state.WeeklyAllowanceUsed = 6;
        Assert.False(_runner.Start(Zhloe));
        Assert.Contains("all 6 of its own deliveries", _runner.StatusLine);

        _state.Used = 2;                            // the client has room; the week does not
        _state.WeeklyAllowanceUsed = 12;
        Assert.False(_runner.Start(Zhloe));
        Assert.Contains("weekly allowance is spent", _runner.StatusLine);
        Assert.Contains("Tuesday 08:00 UTC", _runner.StatusLine);
    }

    [Fact]
    public void A_run_is_capped_by_what_the_week_has_left()
    {
        _game.Bag[ItemId] = 6;
        _state.WeeklyAllowanceUsed = 10;            // two left for the week, six for the client
        Assert.True(_runner.Start(Zhloe));
        Assert.Equal(2, _runner.Target);
    }

    [Fact]
    public void A_locked_client_or_unloaded_data_is_refused_before_anything_moves()
    {
        _state.Unlocked = false;
        Assert.False(_runner.Start(Zhloe));
        Assert.Contains("not unlocked", _runner.StatusLine);

        _state.Unlocked = true;
        _state.DataLoaded = false;
        Assert.False(_runner.Start(Zhloe));
        Assert.Contains("Custom Deliveries window", _runner.StatusLine);
    }

    [Fact]
    public void A_refused_turn_in_retries_and_eventually_faults()
    {
        _game.Bag[ItemId] = 6;
        _game.RefuseCommit = true;
        Assert.True(_runner.Start(Zhloe));
        Run(frames: 200);

        Assert.Equal(DeliveryRunState.Faulted, _runner.State);
        Assert.Equal(0, _game.Committed);
    }

    /// <summary>
    /// Picking a route closes the supply window and opens the trade one. Reading "supply window
    /// gone" as a failure mid-transition faulted a run that had just delivered successfully.
    /// </summary>
    [Fact]
    public void The_gap_between_the_two_windows_is_not_a_failure()
    {
        _game.Bag[ItemId] = 6;
        Assert.True(_runner.Start(Zhloe, limit: 1));

        // Craft is skipped (stocked), travel is instant, then: supply open, route picked, and the
        // client shuts the supply window before the trade window is acknowledged.
        for (var i = 0; i < 10 && _runner.State != DeliveryRunState.TurnIn; i++) _runner.Tick();
        Assert.Equal(DeliveryRunState.TurnIn, _runner.State);

        _runner.Tick();                          // opens the route
        _game.SupplyOpen = false;                // ...and the supply window goes away
        _world.UtcNow = _world.UtcNow.AddSeconds(2);
        _runner.Tick();

        Assert.NotEqual(DeliveryRunState.Faulted, _runner.State);
        Assert.Equal(1, _game.Committed);
        Assert.Equal(DeliveryRunState.Done, _runner.State);
    }

    [Fact]
    public void A_client_that_never_opens_the_window_faults_at_the_interact()
    {
        _game.Bag[ItemId] = 6;
        _game.SupplyOpen = false;
        Assert.True(_runner.Start(Zhloe, limit: 1));
        Run(frames: 200);

        Assert.Equal(DeliveryRunState.Faulted, _runner.State);
        Assert.Contains("could not open the delivery window", _runner.StatusLine);
    }

    /// <summary>Tolerating the transition must not mean waiting forever for a window that is gone.</summary>
    [Fact]
    public void Both_windows_vanishing_before_the_first_hand_over_still_faults()
    {
        _game.Bag[ItemId] = 6;
        Assert.True(_runner.Start(Zhloe, limit: 1));
        for (var i = 0; i < 10 && _runner.State != DeliveryRunState.TurnIn; i++) _runner.Tick();
        Assert.Equal(DeliveryRunState.TurnIn, _runner.State);

        _game.SupplyOpen = false;                // and OpenRoute will never be reached
        Run(frames: 200);

        Assert.Equal(DeliveryRunState.Faulted, _runner.State);
        Assert.Contains("never came up", _runner.StatusLine);
        Assert.Equal(0, _game.Committed);
    }
}
