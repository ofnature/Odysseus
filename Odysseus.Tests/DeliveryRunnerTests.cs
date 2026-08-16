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
        public (int Current, int Max) Satisfaction(DeliveryClient c) => (0, 0);
    }

    private sealed class Rewards : IDeliveryRewards
    {
        public int PerTurnIn { get; set; } = 100;
        public IReadOnlyDictionary<int, int> PerDelivery(DeliveryClient c, int rank, bool bonus = false)
            => new Dictionary<int, int> { [2] = PerTurnIn };
        public int SatisfactionPerDelivery(DeliveryClient c, int rank, bool bonus = false) => 0;
    }

    private sealed class Bonus : IDeliveryBonus
    {
        public int WeekRow => 0;
        public string WeekSource => "test";
        public BonusFlags For(DeliveryClient c) => BonusFlags.None;
    }

    private sealed class Requests : IDeliveryRequests
    {
        public List<DeliveryRequest> Items { get; } =
            [new(DeliveryRoute.Craft, 0, ItemId, "Rroneek Cheese", 500, false)];
        public IReadOnlyList<DeliveryRequest> For(DeliveryClient c, int rank) => Items;
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
        public ushort? Recipe { get; set; } = 4242;
        public ushort? ForItem(uint itemId) => Recipe;
    }

    /// <summary>The supply and trade windows, faked as a two-step handshake per turn-in.</summary>
    private sealed class Game : IDeliveryWorld
    {
        public Dictionary<uint, int> Bag { get; } = new();
        public bool SupplyOpen { get; set; } = true;
        public bool TradeOpen { get; set; }
        public bool RefuseCommit { get; set; }
        public int Committed { get; private set; }

        public bool IsSupplyOpen(DeliveryClient c) => SupplyOpen;
        public void OpenRoute(DeliveryRoute route) => TradeOpen = true;
        public bool IsTradeOpen(uint itemId) => TradeOpen;
        public int ItemCount(uint itemId) => Bag.GetValueOrDefault(itemId);

        public bool CommitTrade(DeliveryRoute route)
        {
            if (RefuseCommit) return false;
            Committed++;
            TradeOpen = false;
            Bag[ItemId] = Math.Max(0, Bag.GetValueOrDefault(ItemId) - 1);
            return true;
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
    private readonly ScripLedger _scrips;
    private readonly DeliveryRunner _runner;
    private readonly List<string> _log = [];

    public DeliveryRunnerTests()
    {
        _scrips = new ScripLedger([Purple], _currency, new DeliveryCatalog([Zhloe]), _state, _rewards, new Bonus());
        _runner = new DeliveryRunner(_world, _game, _state, _requests, _scrips, _crafter, _recipes,
            new StepExecutor(_world), _log.Add);
        _world.PlayerPosition = Zhloe.Position;   // standing at the client
        _world.Spawned.Add(Zhloe.NpcDataId);
    }

    /// <summary>Run frames until the state settles or the budget runs out.</summary>
    private void Run(int frames = 60)
    {
        for (var i = 0; i < frames; i++)
        {
            if (_runner.State is DeliveryRunState.Done or DeliveryRunState.Faulted
                or DeliveryRunState.Blocked or DeliveryRunState.Idle)
                return;
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

    [Fact]
    public void A_short_bag_goes_to_artisan_for_exactly_what_is_missing()
    {
        _game.Bag[ItemId] = 2;
        _state.Used = 1;                            // 5 to do, 2 in hand
        Assert.True(_runner.Start(Zhloe));
        _runner.Tick();

        Assert.Equal((4242, 3), _crafter.Asked.Single());
    }

    /// <summary>The cap is a hard stop: refuse before the first turn-in, not after.</summary>
    [Fact]
    public void Starting_at_the_cap_is_refused_with_the_reason()
    {
        _currency.Amounts[Purple.ItemId] = 3950;    // +100 would spill 50
        _game.Bag[ItemId] = 6;

        Assert.False(_runner.Start(Zhloe));
        Assert.Contains("50 would be lost", _runner.StatusLine);
        Assert.Equal(0, _game.Committed);
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
        _runner.Tick();                             // asks Artisan for six
        Assert.Single(_crafter.Asked);
        _runner.Tick();                             // Artisan is not running and the bag is still empty

        Assert.Equal(DeliveryRunState.Blocked, _runner.State);
        Assert.Contains("does not buy ingredients yet", _runner.StatusLine);
        Assert.Single(_crafter.Asked);              // and it did not ask again
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
    public void A_refused_turn_in_faults_instead_of_retrying_forever()
    {
        _game.Bag[ItemId] = 6;
        _game.RefuseCommit = true;
        Assert.True(_runner.Start(Zhloe));
        Run();

        Assert.Equal(DeliveryRunState.Faulted, _runner.State);
        Assert.Equal(0, _game.Committed);
    }
}
