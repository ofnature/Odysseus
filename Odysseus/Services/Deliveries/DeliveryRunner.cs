using System;
using System.Linq;
using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Services.Deliveries;

/// <summary>The gathering handoff. <c>GatherBuddyIpc</c> satisfies this as it stands.</summary>
public interface IGatherer
{
    bool Available { get; }
    int Version { get; }
    bool IsRunning { get; }
    /// <summary>Running but idle — it cannot reach anything on its list right now.</summary>
    bool IsWaiting { get; }
    string Status { get; }
    bool Start();
    void Stop();
}

/// <summary>The crafting handoff. <c>ArtisanIpc</c> satisfies this as it stands.</summary>
public interface ICrafter
{
    bool Available { get; }
    bool CraftItem(ushort recipeId, int amount);
    bool IsCrafting { get; }
    void StopCrafting();
}

/// <summary>Why a run stopped — decides what the popup is headed, since not every stop is the cap.</summary>
public enum DeliveryStop
{
    None,
    /// <summary>The next turn-in would spill scrip over the cap.</summary>
    ScripCap,
    /// <summary>The item could not be made — no materials, no Artisan, no recipe.</summary>
    Materials,
    /// <summary>Something about the client or character is not ready.</summary>
    Setup,
    /// <summary>The game did not do what was asked.</summary>
    Fault,
}

public enum DeliveryRunState
{
    Idle,
    /// <summary>Waiting on Artisan to make the requested item.</summary>
    Craft,
    /// <summary>Waiting for the requested item to be gathered or fished.</summary>
    Gather,
    /// <summary>Travelling to the client.</summary>
    Travel,
    /// <summary>Buying ingredients from the merchant standing near the client.</summary>
    Shop,
    /// <summary>Talking to the client to open the supply window.</summary>
    Interact,
    /// <summary>Handing items over.</summary>
    TurnIn,
    /// <summary>Stopped deliberately — the reason is in <see cref="DeliveryRunner.StatusLine"/>.</summary>
    Blocked,
    Done,
    Faulted,
}

/// <summary>
/// Runs one client's craft deliveries for the week: make what they asked for, go to them, hand it
/// over, repeat until the allowance is spent.
///
/// <para>
/// The scrip cap is a hard stop, not a warning. Before every single turn-in
/// <see cref="ScripLedger.MayTurnIn"/> is asked, and a refusal ends the run in
/// <see cref="DeliveryRunState.Blocked"/> with the reason — a turn-in that spills over the cap
/// cannot be taken back, so it is never worth guessing.
/// </para>
///
/// <para>
/// All three routes turn in the same way, so gather and fish share everything but the sourcing:
/// craft buys and crafts, while gather and fish check the bag and — when it is short — stop with
/// the item, the collectability, the job and the place to find it. Nothing walks to a node yet.
/// </para>
///
/// <para>
/// Travel comes before shopping and crafting on purpose: every client has a merchant beside them
/// stocking exactly what their craft needs, so getting there first is what makes vendor-only
/// sourcing enough. Anything no nearby vendor sells is named and the run stops — nothing is bought
/// off the market board.
/// </para>
/// </summary>
public sealed class DeliveryRunner
{
    private static readonly TimeSpan InteractStall = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TurnInStall = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CraftStall = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReinteractGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TurnInGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShopGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShopStall = TimeSpan.FromSeconds(30);
    /// <summary>How long GatherBuddy may sit idle before we take it as stuck. Timed nodes are slow.</summary>
    private static readonly TimeSpan GatherWait = TimeSpan.FromSeconds(90);

    private readonly IStepWorld _world;
    private readonly IDeliveryWorld _game;
    private readonly IDeliveryState _state;
    private readonly IDeliveryRequests _requests;
    private readonly ScripLedger _scrips;
    private readonly ICrafter _crafter;
    private readonly IRecipeLookup _recipes;
    private readonly IIngredientSource _ingredients;
    private readonly IGatheringSource _gathering;
    private readonly IGatherer _gatherer;
    private readonly StepExecutor _travel;
    private readonly Func<int> _preferredCraftType;
    private readonly Action<string> _log;

    private DeliveryClient? _client;
    private DeliveryRequest? _request;
    private DateTime _phaseStart;
    private DateTime _lastAction;
    private int _delivered;
    private int _target;
    private bool _craftStarted;
    private bool _gatherStarted;
    private RecipeOption? _recipe;
    private DeliveryRoute _route = DeliveryRoute.Craft;

    public DeliveryRunner(IStepWorld world, IDeliveryWorld game, IDeliveryState state, IDeliveryRequests requests,
        ScripLedger scrips, ICrafter crafter, IRecipeLookup recipes, IIngredientSource ingredients,
        IGatheringSource gathering, IGatherer gatherer, StepExecutor travel, Func<int> preferredCraftType,
        Action<string> log)
    {
        _preferredCraftType = preferredCraftType;
        _ingredients = ingredients;
        _gathering = gathering;
        _gatherer = gatherer;
        _world = world;
        _game = game;
        _state = state;
        _requests = requests;
        _scrips = scrips;
        _crafter = crafter;
        _recipes = recipes;
        _travel = travel;
        _log = log;
    }

    public DeliveryRunState State { get; private set; } = DeliveryRunState.Idle;
    public string StatusLine { get; private set; } = string.Empty;
    /// <summary>Why the last stop happened; <see cref="DeliveryStop.None"/> while running.</summary>
    public DeliveryStop StoppedBecause { get; private set; } = DeliveryStop.None;
    /// <summary>The run is over, one way or another — nothing more will happen without a new Start.</summary>
    public bool IsFinished => State is DeliveryRunState.Idle or DeliveryRunState.Done
        or DeliveryRunState.Faulted or DeliveryRunState.Blocked;
    public DeliveryClient? Client => _client;
    /// <summary>Deliveries handed over since <see cref="Start"/>.</summary>
    public int Delivered => _delivered;

    /// <summary>What the run is aiming for — the whole allowance, or fewer on a test run.</summary>
    public int Target => _target;

    /// <summary>What this client is asking for on the running route, once a run has started.</summary>
    public DeliveryRequest? Request => _request;
    public DeliveryRoute Route => _route;

    /// <summary>Begin this client's craft deliveries. False with a reason in <see cref="StatusLine"/>.</summary>
    /// <param name="limit">
    /// Cap the run at this many deliveries. 0 means the whole remaining allowance. 1 is the
    /// one-shot used to check a client end to end before trusting it with the week.
    /// </param>
    public bool Start(DeliveryClient client, int limit = 0) => Start(client, DeliveryRoute.Craft, limit);

    /// <inheritdoc cref="Start(DeliveryClient, int)"/>
    public bool Start(DeliveryClient client, DeliveryRoute route, int limit = 0)
    {
        if (!_state.IsUnlocked(client))
            return Refuse(DeliveryStop.Setup, $"{client.Name} is not unlocked yet.");
        if (!_state.DataLoaded)
            return Refuse(DeliveryStop.Setup,
                "Delivery data has not loaded — open the game's Custom Deliveries window once, then start.");

        var remaining = _scrips.RemainingDeliveries(client);
        if (remaining <= 0)
            return Refuse(DeliveryStop.Setup, _scrips.WeeklyRemaining <= 0
                ? $"The weekly allowance is spent — all {DeliveryLimits.WeeklyAllowance} deliveries are used across " +
                  "every client. It resets with the weekly reset (Tuesday 08:00 UTC)."
                : $"{client.Name} has taken all {client.DeliveriesPerWeek} of its own deliveries this week.");

        var (allowed, reason) = _scrips.MayTurnIn(client, route);
        if (!allowed)
            return Refuse(DeliveryStop.ScripCap, reason!);

        var request = _requests.For(client, _state.Rank(client)).FirstOrDefault(r => r.Route == route);
        if (request is null)
            return Refuse(DeliveryStop.Setup, $"Could not work out what {client.Name} wants for the {route} route.");
        if (client.NpcDataId == 0)
            return Refuse(DeliveryStop.Setup, $"{client.Name} has no NPC position in the sheet.");

        _client = client;
        _request = request;
        _route = route;
        _delivered = 0;
        _target = limit > 0 ? Math.Min(limit, remaining) : remaining;
        _craftStarted = false;
        _gatherStarted = false;
        _recipe = null;
        StoppedBecause = DeliveryStop.None;
        // Travel first: the merchant that stocks the ingredients stands beside the client.
        Enter(DeliveryRunState.Travel);
        _log($"{client.Name}: {_target} deliver{(_target == 1 ? "y" : "ies")} of {request.ItemName} " +
             $"(collectability {request.CollectabilityHigh}){(limit > 0 ? " — test run" : "")}.");
        return true;
    }

    public void Stop()
    {
        _travel.Cancel();
        if (_crafter.IsCrafting) _crafter.StopCrafting();
        if (_gatherStarted) _gatherer.Stop();
        _game.CloseShop();
        _client = null;
        State = DeliveryRunState.Idle;
        StoppedBecause = DeliveryStop.None;
    }

    /// <summary>Refuse to start, recording why so the popup can be headed correctly.</summary>
    private bool Refuse(DeliveryStop kind, string reason)
    {
        StoppedBecause = kind;
        StatusLine = reason;
        return false;
    }

    public void Tick()
    {
        if (_client is null || IsFinished)
            return;
        try
        {
            switch (State)
            {
                case DeliveryRunState.Travel: TickTravel(); break;
                case DeliveryRunState.Shop: TickShop(); break;
                case DeliveryRunState.Craft: TickCraft(); break;
                case DeliveryRunState.Gather: TickGather(); break;
                case DeliveryRunState.Interact: TickInteract(); break;
                case DeliveryRunState.TurnIn: TickTurnIn(); break;
            }
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// How many more we still need in the bag to finish the run. Counted against the client's
    /// minimum collectability — anything below it cannot be handed over, so counting it would leave
    /// the run thinking it was ready and then stalling at the trade window.
    /// </summary>
    private int Shortfall()
        => Math.Max(0, _target - _delivered - _game.ItemCount(_request!.ItemId, _request.CollectabilityLow));

    private void TickCraft()
    {
        var client = _client!;
        var request = _request!;
        var short_ = Shortfall();

        if (short_ <= 0)
        {
            if (_craftStarted && _crafter.IsCrafting) _crafter.StopCrafting();
            Enter(DeliveryRunState.Interact);
            return;
        }

        if (_crafter.IsCrafting)
        {
            StatusLine = $"{client.Name}: Artisan is crafting {request.ItemName} ({short_} to go)";
            _phaseStart = _world.UtcNow; // it is making progress; don't time it out
            return;
        }

        if (_craftStarted)
        {
            // Artisan stopped with the bag still short. Anything a nearby vendor sells has already
            // been bought, so what is left is something only you can supply.
            var short2 = _recipe is null
                ? []
                : _ingredients.Plan(_recipe.RecipeId, short_, id => _game.ItemCount(id)).Where(n => n.Missing > 0).ToList();
            var missing = short2.Count > 0
                ? " Still short: " + string.Join(", ", short2.Select(n => $"{n.Missing} × {n.Name}")) + "."
                : string.Empty;
            Block(DeliveryStop.Materials,
                  $"{client.Name}: Artisan stopped with {short_} × {request.ItemName} still needed.{missing} " +
                  "Nothing nearby sells the rest, so stock up and start it again.");
            return;
        }

        if (!_crafter.Available)
        {
            Block(DeliveryStop.Materials,
                  $"{client.Name}: {short_} × {request.ItemName} needed and Artisan is not installed. " +
                  "Craft them yourself, or install Artisan and start it again.");
            return;
        }

        if (Recipe() is not { } recipe)
        {
            Block(DeliveryStop.Materials, $"{client.Name}: no recipe found for {request.ItemName}.");
            return;
        }

        if (!_crafter.CraftItem(recipe.RecipeId, short_))
        {
            Block(DeliveryStop.Materials, $"{client.Name}: Artisan would not take the craft for {request.ItemName}.");
            return;
        }
        _craftStarted = true;
        StatusLine = $"{client.Name}: asked Artisan for {short_} × {request.ItemName} as {recipe.JobName}";

        if (_world.UtcNow - _phaseStart > CraftStall)
            Fault($"{client.Name}: Artisan never started crafting.");
    }

    private void TickTravel()
    {
        var client = _client!;
        if (_world.TerritoryId == client.TerritoryId
            && Vector3.Distance(_world.PlayerPosition, client.Position) <= StepExecutor.DefaultStopDistance + StepExecutor.ArrivalSlack)
        {
            _travel.Cancel();
            // Buying and crafting only exist for the craft route; the others just check the bag.
            Enter(_route == DeliveryRoute.Craft ? DeliveryRunState.Shop : DeliveryRunState.Gather);
            return;
        }

        if (_travel.Status != StepStatus.Running)
        {
            _travel.Begin(new QuestStep
            {
                Kind = StepKind.WalkTo,
                KindName = "WalkTo",
                Position = client.Position,
                TerritoryId = client.TerritoryId,
            });
        }
        StatusLine = $"{client.Name}: on the way";
        if (_travel.Tick() == StepStatus.Failed)
            Fault($"{client.Name}: could not get there — {_travel.FailReason}");
    }

    /// <summary>The recipe chosen for this run, resolved once so shopping and crafting agree.</summary>
    private RecipeOption? Recipe()
    {
        if (_recipe is not null) return _recipe;
        return _recipe = RecipePicker.Pick(_recipes.OptionsFor(_request!.ItemId), _preferredCraftType(), _game.CurrentCraftType);
    }

    /// <summary>
    /// Gathering is handed to GatherBuddy, which can only be switched on — it takes no request. So
    /// this switches it on and watches our own bag until the count is met, rather than asking it
    /// for progress it cannot report. Fishing has no handoff at all and always stops.
    /// </summary>
    private void TickGather()
    {
        var client = _client!;
        var request = _request!;
        var short_ = Shortfall();

        if (short_ <= 0)
        {
            if (_gatherStarted) _gatherer.Stop();
            Enter(DeliveryRunState.Interact);
            return;
        }

        if (_route == DeliveryRoute.Fish || !_gatherer.Available)
        {
            BlockNeedingItems(short_, _gatherer.Available
                ? "GatherBuddy does not fish"
                : "GatherBuddy is not installed");
            return;
        }

        if (_gatherer.IsRunning)
        {
            // It cannot say how far along it is, so the bag is the progress meter. Waiting means it
            // has nothing to do — a timed node, the wrong job, or the item not on any of its lists.
            if (_gatherer.IsWaiting)
            {
                if (_world.UtcNow - _phaseStart <= GatherWait) return;
                var why = _gatherer.Status;   // Block switches it off; taking the reason first.
                BlockNeedingItems(short_,
                    $"GatherBuddy has been idle for {GatherWait.TotalSeconds:N0}s" + (why.Length > 0 ? $" — \"{why}\"" : "") +
                    ". Check the item is on one of its auto-gather lists");
                return;
            }
            StatusLine = $"{client.Name}: GatherBuddy is gathering {request.ItemName} ({short_} to go)";
            _phaseStart = _world.UtcNow;    // it is working; the idle clock only runs while waiting
            return;
        }

        if (_gatherStarted)
        {
            // It switched itself off with the bag still short — finished its list, or gave up.
            BlockNeedingItems(short_, "GatherBuddy stopped on its own");
            return;
        }

        if (!_gatherer.Start())
        {
            BlockNeedingItems(short_, "GatherBuddy would not start");
            return;
        }
        _gatherStarted = true;
        _phaseStart = _world.UtcNow;
        StatusLine = $"{client.Name}: asked GatherBuddy for {short_} × {request.ItemName}";
        _log(StatusLine);
    }

    /// <summary>Stop, saying what is missing and where it is found — the useful half of "cannot".</summary>
    private void BlockNeedingItems(int missing, string because)
    {
        var request = _request!;
        var where = _gathering.For(request.ItemId) is { } origin ? $" Found at {origin.Describe()}." : string.Empty;
        Block(DeliveryStop.Materials,
              $"{_client!.Name}: {missing} × {request.ItemName} needed at collectability {request.CollectabilityLow} " +
              $"or better, and {because}.{where} Get them and start it again — the travel and turn-in are handled.");
    }

    private void TickShop()
    {
        var client = _client!;
        var short_ = Shortfall();
        if (short_ <= 0 || Recipe() is not { } recipe)
        {
            _game.CloseShop();
            Enter(DeliveryRunState.Craft);
            return;
        }

        var needs = _ingredients.Plan(recipe.RecipeId, short_, id => _game.ItemCount(id));
        var buy = needs.FirstOrDefault(n => n.CanBuy && _world.PositionOfDataId(n.VendorDataId) is not null);

        if (buy is null)
        {
            // Either nothing is missing, or nobody in reach sells what is.
            var stuck = needs.Where(n => n.Missing > 0).ToList();
            _game.CloseShop();
            if (stuck.Count > 0)
                _log($"{client.Name}: no nearby vendor for {string.Join(", ", stuck.Select(n => $"{n.Missing} × {n.Name}"))} — leaving it to Artisan.");
            Enter(DeliveryRunState.Craft);
            return;
        }

        if (_game.ShopBusy(buy.ShopId))
        {
            _phaseStart = _world.UtcNow;  // a purchase is going through; that is progress
            return;
        }

        if (_game.Gil < buy.GilForMissing)
        {
            Block(DeliveryStop.Materials,
                  $"{client.Name}: {buy.Missing} × {buy.Name} costs {buy.GilForMissing:N0} gil and you have {_game.Gil:N0}.");
            return;
        }

        var vendorPosition = _world.PositionOfDataId(buy.VendorDataId)!.Value;
        if (Vector3.Distance(_world.PlayerPosition, vendorPosition) > StepExecutor.DefaultStopDistance + StepExecutor.ArrivalSlack)
        {
            if (_travel.Status != StepStatus.Running)
                _travel.Begin(new QuestStep
                {
                    Kind = StepKind.WalkTo, KindName = "WalkTo",
                    Position = vendorPosition, TerritoryId = client.TerritoryId,
                });
            StatusLine = $"{client.Name}: walking to {buy.VendorName}";
            if (_travel.Tick() == StepStatus.Failed)
                Fault($"{client.Name}: could not reach {buy.VendorName} — {_travel.FailReason}");
            return;
        }
        _travel.Cancel();

        StatusLine = $"{client.Name}: buying {buy.Missing} × {buy.Name} from {buy.VendorName}";
        if (_world.UtcNow - _lastAction < ShopGap) return;
        _lastAction = _world.UtcNow;

        if (!_game.IsShopOpen(buy.ShopId))
        {
            if (!_game.OpenShop(buy.VendorDataId, buy.ShopId))
                _log($"{client.Name}: could not open {buy.VendorName}'s shop yet.");
        }
        else if (!_game.BuyFromShop(buy.ShopId, buy.ItemId, buy.Missing))
        {
            Block(DeliveryStop.Materials, $"{client.Name}: {buy.VendorName} does not stock {buy.Name}.");
            return;
        }
        else
        {
            _phaseStart = _world.UtcNow;
            _log($"{client.Name}: bought {buy.Missing} × {buy.Name} for {buy.GilForMissing:N0} gil.");
        }

        if (_world.UtcNow - _phaseStart > ShopStall)
            Fault($"{client.Name}: shopping at {buy.VendorName} stalled.");
    }

    private void TickInteract()
    {
        var client = _client!;
        if (_game.IsSupplyOpen(client))
        {
            Enter(DeliveryRunState.TurnIn);
            return;
        }

        StatusLine = $"{client.Name}: opening the delivery window";
        if (_world.IsAddonVisible("SelectString")) { _world.SelectStringIndex(0); Bump(); return; }
        if (_world.IsOccupied) return; // mid-conversation

        if (_world.UtcNow - _lastAction > ReinteractGap)
        {
            if (!_world.TryInteractWithDataId(client.NpcDataId))
                _log($"{client.Name}: not in reach ({client.NpcDataId}).");
            _lastAction = _world.UtcNow;
        }
        if (_world.UtcNow - _phaseStart > InteractStall)
            Fault($"{client.Name}: could not open the delivery window.");
    }

    private void TickTurnIn()
    {
        var client = _client!;
        var request = _request!;

        // Re-ask the cap before every hand-over: each one moves the balance.
        if (_delivered >= _target || _scrips.RemainingDeliveries(client) <= 0)
        {
            Finish();
            return;
        }

        var (allowed, reason) = _scrips.MayTurnIn(client, _route);
        if (!allowed)
        {
            Block(DeliveryStop.ScripCap, reason!);
            return;
        }
        if (_game.ItemCount(request.ItemId) <= 0)
        {
            Block(DeliveryStop.Materials, $"{client.Name}: out of {request.ItemName} after {_delivered} deliveries.");
            return;
        }

        StatusLine = $"{client.Name}: turning in {request.ItemName} ({_delivered}/{_target})";
        if (_world.UtcNow - _lastAction < TurnInGap) return;

        // The trade window first: picking a route closes the supply window and opens this one, so
        // asking "is the supply window still up?" at the wrong moment reads as a failure when it is
        // just the handover between the two.
        if (_game.IsTradeOpen(request.ItemId))
        {
            if (!_game.CommitTrade(_route))
            {
                // Retry rather than fault: the three events need the agent to have caught up, and
                // a refusal one frame can succeed the next. The stall clock is the real backstop.
                _lastAction = _world.UtcNow;
                if (_world.UtcNow - _phaseStart > TurnInStall)
                    Fault($"{client.Name}: the turn-in was refused by the game.");
                return;
            }
            _delivered++;
            _lastAction = _world.UtcNow;
            _phaseStart = _world.UtcNow;
            _log($"{client.Name}: delivered {_delivered}/{_target}.");
            if (_delivered >= _target) Finish();
            return;
        }

        if (_game.IsSupplyOpen(client))
        {
            _game.OpenRoute(_route);
            _lastAction = _world.UtcNow;
            return;
        }

        // Neither window is up. That is normal for a beat — mid-transition, or the client closed
        // both after a hand-over — so go back and talk again rather than calling it a failure, and
        // only give up once the stall clock runs out.
        if (_delivered > 0)
        {
            Enter(DeliveryRunState.Interact);
            return;
        }
        if (_world.UtcNow - _phaseStart > TurnInStall)
            Fault($"{client.Name}: the delivery window never came up.");
    }

    private void Finish()
    {
        State = DeliveryRunState.Done;
        var weekly = _scrips.WeeklyRemaining;
        StatusLine = weekly <= 0
            ? $"{_client!.Name}: {_delivered} delivered — the weekly allowance of {DeliveryLimits.WeeklyAllowance} is now spent."
            : $"{_client!.Name}: {_delivered} delivered, {weekly} left in the weekly allowance.";
        _log(StatusLine);
    }

    private void Bump()
    {
        _lastAction = _world.UtcNow;
        _phaseStart = _world.UtcNow;
    }

    private void Enter(DeliveryRunState state)
    {
        State = state;
        _phaseStart = _world.UtcNow;
        _lastAction = default;
    }

    /// <summary>A deliberate stop — not an error. The reason is meant to be shown to the player.</summary>
    private void Block(DeliveryStop kind, string reason)
    {
        _travel.Cancel();
        if (_crafter.IsCrafting) _crafter.StopCrafting();
        if (_gatherStarted) { _gatherer.Stop(); _gatherStarted = false; }
        State = DeliveryRunState.Blocked;
        StoppedBecause = kind;
        StatusLine = reason;
        _log($"Stopped: {reason}");
    }

    private void Fault(string reason)
    {
        _travel.Cancel();
        if (_crafter.IsCrafting) _crafter.StopCrafting();
        if (_gatherStarted) { _gatherer.Stop(); _gatherStarted = false; }
        State = DeliveryRunState.Faulted;
        StoppedBecause = DeliveryStop.Fault;
        StatusLine = reason;
        _log($"FAULT: {reason}");
    }
}
