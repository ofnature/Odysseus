using System;
using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Quest;

/// <summary>What the seller needs from the game. One seam, so the state machine is testable.</summary>
public interface ISellWorld
{
    DateTime UtcNow { get; }

    /// <summary>A vendor's shop window is open — that window is the session, as everywhere else.</summary>
    bool ShopOpen { get; }

    /// <summary>How many of an item are in the bags.</summary>
    int Held(uint itemId);

    /// <summary>Every stack of an item, largest first is not assumed — the caller picks.</summary>
    IReadOnlyList<BagStack> Stacks(uint itemId);

    /// <summary>Split a quantity off a stack into a new one. False when the call was refused.</summary>
    bool Split(BagStack stack, int quantity);

    /// <summary>Ask for a stack's context menu. False means the inventory was not up and has been asked to open.</summary>
    bool OpenMenu(BagStack stack);

    /// <summary>Click Sell on the open menu.</summary>
    InventoryMenu.ClickResult ClickSell();

    void Log(string message);
}

/// <summary>
/// Sells what the quests handed over, at whatever vendor is open.
///
/// <para>
/// The whole design constraint is that the context menu's Sell takes a <b>whole stack</b> and the
/// ledger owes a <b>number</b>. So a stack larger than what is owed is split first and the
/// split-off stack is what gets sold — the same shape the gil-cap seller uses, and the reason a
/// reward of one Venture can never take the two hundred sitting behind it.
/// </para>
///
/// <para>
/// Every sale is verified by the bag count dropping rather than by the click returning, and only a
/// verified drop draws down the ledger. An item that will not sell is given up on after a few
/// tries: a vendor that refuses one thing must not stall the other nine behind it.
/// </para>
/// </summary>
public sealed class RewardSeller
{
    private enum Phase { Idle, Opening, Clicking, Verifying, Splitting }

    /// <summary>One action per beat — the menu needs a frame to build and a sale is a round trip.</summary>
    private static readonly TimeSpan ActionGap = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(4);
    private const int MaxAttempts = 3;

    private readonly ISellWorld _world;
    private readonly RewardLedger _ledger;
    private readonly Func<bool> _enabled;
    private readonly Action _save;

    private Phase _phase = Phase.Idle;
    private DateTime _lastAction;
    private DateTime _phaseStart;
    private uint _item;
    private int _want;
    private int _heldBefore;
    private BagStack _stack;
    private int _attempts;

    /// <param name="enabled">The user's toggle, read live so switching it off mid-run stops it.</param>
    /// <param name="save">Persist the ledger — a balance must survive the trip to the vendor.</param>
    public RewardSeller(ISellWorld world, RewardLedger ledger, Func<bool> enabled, Action save)
    {
        _world = world;
        _ledger = ledger;
        _enabled = enabled;
        _save = save;
    }

    public string Status { get; private set; } = "idle";

    public bool Busy => _phase != Phase.Idle;

    /// <summary>Items the vendor refused this session; retrying them forever helps nobody.</summary>
    private readonly HashSet<uint> _refused = [];

    public void Tick()
    {
        if (!_enabled() || !_ledger.Any)
        {
            Reset();
            return;
        }

        // The shop window is the session. Losing it mid-sale abandons the attempt rather than
        // firing menu callbacks into whatever replaced it.
        if (!_world.ShopOpen)
        {
            if (_phase != Phase.Idle)
            {
                Status = "vendor closed — stopped";
                Reset();
            }
            return;
        }

        var now = _world.UtcNow;
        if (now - _lastAction < ActionGap)
            return;
        _lastAction = now;

        if (_phase == Phase.Idle)
        {
            BeginNext();
            return;
        }

        if (now - _phaseStart > StepTimeout)
        {
            Give(_item, "timed out");
            return;
        }

        switch (_phase)
        {
            case Phase.Splitting: TickSplit(); break;
            case Phase.Opening: TickOpen(); break;
            case Phase.Clicking: TickClick(); break;
            case Phase.Verifying: TickVerify(); break;
        }
    }

    /// <summary>Pick the next thing owed, and decide whether it can be sold whole or must be split.</summary>
    private void BeginNext()
    {
        foreach (var pending in _ledger.Pending)
        {
            if (_refused.Contains(pending.ItemId)) continue;
            var owed = _ledger.Owed(pending.ItemId, _world.Held(pending.ItemId));
            if (owed <= 0)
            {
                // Banked but no longer in the bag — used, traded or already gone.
                _ledger.Sold(pending.ItemId, pending.Quantity);
                _save();
                continue;
            }

            var stacks = _world.Stacks(pending.ItemId);
            if (stacks.Count == 0)
            {
                _ledger.Sold(pending.ItemId, pending.Quantity);
                _save();
                continue;
            }

            _item = pending.ItemId;
            _want = owed;
            _heldBefore = _world.Held(_item);

            // Sell a stack that is exactly what is owed, or smaller — no split needed. Otherwise
            // split the owed amount off the smallest stack that can cover it.
            if (stacks.FirstOrDefault(s => s.Quantity <= owed) is { Quantity: > 0 } sellable)
            {
                _stack = sellable;
                _want = sellable.Quantity;
                Enter(Phase.Opening);
                return;
            }

            _stack = stacks.OrderBy(s => s.Quantity).First();
            Enter(Phase.Splitting);
            return;
        }

        Status = _refused.Count > 0 ? $"nothing sellable ({_refused.Count} refused)" : "idle";
    }

    private void TickSplit()
    {
        // A split that has landed shows up as a stack of exactly what was asked for.
        if (_world.Stacks(_item).FirstOrDefault(s => s.Quantity == _want) is { Quantity: > 0 } split)
        {
            _stack = split;
            Enter(Phase.Opening);
            return;
        }
        if (_attempts++ >= MaxAttempts)
        {
            Give(_item, "could not split the stack (bags full?)");
            return;
        }
        if (!_world.Split(_stack, _want))
            Give(_item, "the split was refused");
    }

    private void TickOpen()
    {
        Status = $"selling {_want} × item {_item}";
        // False means the inventory window was not up; it has been asked for, so try again.
        if (_world.OpenMenu(_stack))
            Enter(Phase.Clicking);
    }

    private void TickClick()
    {
        switch (_world.ClickSell())
        {
            case InventoryMenu.ClickResult.NotReady:
                return; // the menu takes a frame to build
            case InventoryMenu.ClickResult.Clicked:
                Enter(Phase.Verifying);
                return;
            default:
                Give(_item, "the vendor menu has no Sell entry here");
                return;
        }
    }

    /// <summary>
    /// The bag dropping is the only signal worth trusting — the callback returns nothing useful
    /// and a sale is a server round trip.
    /// </summary>
    private void TickVerify()
    {
        var now = _world.Held(_item);
        if (now >= _heldBefore)
            return; // still settling; the phase timeout is the way out

        var sold = _heldBefore - now;
        _ledger.Sold(_item, sold);
        _save();
        _world.Log($"Sold {sold} × item {_item} to the vendor.");
        Status = $"sold {sold} × item {_item}";
        _attempts = 0;
        Enter(Phase.Idle);
    }

    /// <summary>Stop trying this item. The ledger keeps the balance; only this session gives up.</summary>
    private void Give(uint itemId, string why)
    {
        _refused.Add(itemId);
        _world.Log($"Leaving item {itemId} unsold — {why}.");
        Status = $"skipped item {itemId}: {why}";
        _attempts = 0;
        Enter(Phase.Idle);
    }

    private void Enter(Phase phase)
    {
        _phase = phase;
        _phaseStart = _world.UtcNow;
    }

    private void Reset()
    {
        _phase = Phase.Idle;
        _attempts = 0;
    }
}
