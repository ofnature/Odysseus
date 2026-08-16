using System;
using System.Linq;
using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Services.Deliveries;

/// <summary>The crafting handoff. <c>ArtisanIpc</c> satisfies this as it stands.</summary>
public interface ICrafter
{
    bool Available { get; }
    bool CraftItem(ushort recipeId, int amount);
    bool IsCrafting { get; }
    void StopCrafting();
}

/// <summary>Finds the recipe that makes an item.</summary>
public interface IRecipeLookup
{
    ushort? ForItem(uint itemId);
}

public enum DeliveryRunState
{
    Idle,
    /// <summary>Waiting on Artisan to make the requested item.</summary>
    Craft,
    /// <summary>Travelling to the client.</summary>
    Travel,
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
/// Only the craft route is run. Gathering and fishing need their own handoffs and are not wired up;
/// <see cref="Start"/> refuses them rather than pretending.
/// </para>
///
/// <para>
/// <b>Ingredients are not bought yet.</b> If the requested item is not already in the inventory the
/// run stops and says what is missing — buying from vendors and the market board is the next piece.
/// </para>
/// </summary>
public sealed class DeliveryRunner
{
    private static readonly TimeSpan InteractStall = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TurnInStall = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CraftStall = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ReinteractGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TurnInGap = TimeSpan.FromSeconds(1);

    private readonly IStepWorld _world;
    private readonly IDeliveryWorld _game;
    private readonly IDeliveryState _state;
    private readonly IDeliveryRequests _requests;
    private readonly ScripLedger _scrips;
    private readonly ICrafter _crafter;
    private readonly IRecipeLookup _recipes;
    private readonly StepExecutor _travel;
    private readonly Action<string> _log;

    private DeliveryClient? _client;
    private DeliveryRequest? _request;
    private DateTime _phaseStart;
    private DateTime _lastAction;
    private int _delivered;
    private int _target;
    private bool _craftStarted;

    public DeliveryRunner(IStepWorld world, IDeliveryWorld game, IDeliveryState state, IDeliveryRequests requests,
        ScripLedger scrips, ICrafter crafter, IRecipeLookup recipes, StepExecutor travel, Action<string> log)
    {
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
    public DeliveryClient? Client => _client;
    /// <summary>Deliveries handed over since <see cref="Start"/>.</summary>
    public int Delivered => _delivered;

    /// <summary>What the run is aiming for — the whole allowance, or fewer on a test run.</summary>
    public int Target => _target;

    /// <summary>What this client is asking for on the craft route, once a run has started.</summary>
    public DeliveryRequest? Request => _request;

    /// <summary>Begin this client's craft deliveries. False with a reason in <see cref="StatusLine"/>.</summary>
    /// <param name="limit">
    /// Cap the run at this many deliveries. 0 means the whole remaining allowance. 1 is the
    /// one-shot used to check a client end to end before trusting it with the week.
    /// </param>
    public bool Start(DeliveryClient client, int limit = 0)
    {
        if (!_state.IsUnlocked(client))
        {
            StatusLine = $"{client.Name} is not unlocked yet.";
            return false;
        }
        if (!_state.DataLoaded)
        {
            StatusLine = "Delivery data has not loaded — open the game's Custom Deliveries window once, then start.";
            return false;
        }

        var remaining = _scrips.RemainingDeliveries(client);
        if (remaining <= 0)
        {
            StatusLine = $"{client.Name} has no deliveries left this week.";
            return false;
        }

        var (allowed, reason) = _scrips.MayTurnIn(client);
        if (!allowed)
        {
            StatusLine = reason!;
            return false;
        }

        var request = _requests.For(client, _state.Rank(client)).FirstOrDefault(r => r.Route == DeliveryRoute.Craft);
        if (request is null)
        {
            StatusLine = $"Could not work out what {client.Name} is asking for.";
            return false;
        }
        if (client.NpcDataId == 0)
        {
            StatusLine = $"{client.Name} has no NPC position in the sheet.";
            return false;
        }

        _client = client;
        _request = request;
        _delivered = 0;
        _target = limit > 0 ? Math.Min(limit, remaining) : remaining;
        _craftStarted = false;
        Enter(DeliveryRunState.Craft);
        _log($"{client.Name}: {_target} deliver{(_target == 1 ? "y" : "ies")} of {request.ItemName} " +
             $"(collectability {request.CollectabilityHigh}){(limit > 0 ? " — test run" : "")}.");
        return true;
    }

    public void Stop()
    {
        _travel.Cancel();
        if (_crafter.IsCrafting) _crafter.StopCrafting();
        _client = null;
        State = DeliveryRunState.Idle;
    }

    public void Tick()
    {
        if (_client is null || State is DeliveryRunState.Idle or DeliveryRunState.Done
            or DeliveryRunState.Faulted or DeliveryRunState.Blocked)
            return;
        try
        {
            switch (State)
            {
                case DeliveryRunState.Craft: TickCraft(); break;
                case DeliveryRunState.Travel: TickTravel(); break;
                case DeliveryRunState.Interact: TickInteract(); break;
                case DeliveryRunState.TurnIn: TickTurnIn(); break;
            }
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>How many more we still need in the bag to finish the run.</summary>
    private int Shortfall() => Math.Max(0, _target - _delivered - _game.ItemCount(_request!.ItemId));

    private void TickCraft()
    {
        var client = _client!;
        var request = _request!;
        var short_ = Shortfall();

        if (short_ <= 0)
        {
            if (_craftStarted && _crafter.IsCrafting) _crafter.StopCrafting();
            Enter(DeliveryRunState.Travel);
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
            // Artisan stopped with the bag still short — out of materials, most likely.
            Block($"{client.Name}: Artisan stopped with {short_} × {request.ItemName} still needed. " +
                  "Odysseus does not buy ingredients yet, so stock up and start it again.");
            return;
        }

        if (!_crafter.Available)
        {
            Block($"{client.Name}: {short_} × {request.ItemName} needed and Artisan is not installed. " +
                  "Craft them yourself, or install Artisan and start it again.");
            return;
        }

        if (_recipes.ForItem(request.ItemId) is not { } recipe)
        {
            Block($"{client.Name}: no recipe found for {request.ItemName}.");
            return;
        }

        if (!_crafter.CraftItem(recipe, short_))
        {
            Block($"{client.Name}: Artisan would not take the craft for {request.ItemName}.");
            return;
        }
        _craftStarted = true;
        StatusLine = $"{client.Name}: asked Artisan for {short_} × {request.ItemName}";

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
            Enter(DeliveryRunState.Interact);
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
        var (allowed, reason) = _scrips.MayTurnIn(client);
        if (!allowed)
        {
            Block(reason!);
            return;
        }

        if (_delivered >= _target || _scrips.RemainingDeliveries(client) <= 0)
        {
            Finish();
            return;
        }
        if (_game.ItemCount(request.ItemId) <= 0)
        {
            Block($"{client.Name}: out of {request.ItemName} after {_delivered} deliveries.");
            return;
        }
        if (!_game.IsSupplyOpen(client))
        {
            // The window closes itself after a hand-over; reopen for the next one.
            if (_delivered > 0 && _delivered < _target) { Enter(DeliveryRunState.Interact); return; }
            Fault($"{client.Name}: the delivery window closed unexpectedly.");
            return;
        }

        StatusLine = $"{client.Name}: turning in {request.ItemName} ({_delivered}/{_target})";
        if (_world.UtcNow - _lastAction < TurnInGap) return;

        if (!_game.IsTradeOpen(request.ItemId))
        {
            _game.OpenRoute(DeliveryRoute.Craft);
            _lastAction = _world.UtcNow;
        }
        else if (_game.CommitTrade(DeliveryRoute.Craft))
        {
            _delivered++;
            _lastAction = _world.UtcNow;
            _phaseStart = _world.UtcNow;
            _log($"{client.Name}: delivered {_delivered}/{_target}.");
        }
        else
        {
            Fault($"{client.Name}: the turn-in was refused by the game.");
            return;
        }

        if (_world.UtcNow - _phaseStart > TurnInStall)
            Fault($"{client.Name}: the turn-in stalled after {_delivered} deliveries.");
    }

    private void Finish()
    {
        State = DeliveryRunState.Done;
        StatusLine = $"{_client!.Name}: {_delivered} delivered.";
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
    private void Block(string reason)
    {
        _travel.Cancel();
        if (_crafter.IsCrafting) _crafter.StopCrafting();
        State = DeliveryRunState.Blocked;
        StatusLine = reason;
        _log($"Stopped: {reason}");
    }

    private void Fault(string reason)
    {
        _travel.Cancel();
        if (_crafter.IsCrafting) _crafter.StopCrafting();
        State = DeliveryRunState.Faulted;
        StatusLine = reason;
        _log($"FAULT: {reason}");
    }
}
