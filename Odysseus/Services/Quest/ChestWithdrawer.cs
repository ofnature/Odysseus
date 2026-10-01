using System;
using System.Collections.Generic;

namespace Odysseus.Services.Quest;

/// <summary>One occupied slot on a Free Company chest page.</summary>
public sealed record ChestStack(int Container, short Slot, uint ItemId, int Quantity);

/// <summary>What the withdrawer needs from the game. One seam, so the state machine is testable.</summary>
public interface IChestWorld
{
    DateTime UtcNow { get; }

    /// <summary>
    /// The Free Company chest window is open. That window <i>is</i> the transfer session — being
    /// stood next to the chest is not enough, and the moment it closes every move stops working.
    /// </summary>
    bool ChestOpen { get; }

    /// <summary>Stacks of an item on the chest pages the game has loaded.</summary>
    IReadOnlyList<ChestStack> ChestStacks(uint itemId);

    /// <summary>How many are in the bags.</summary>
    int Held(uint itemId);

    /// <summary>Move a whole stack into the bags. False when there was nowhere to put it.</summary>
    bool Withdraw(ChestStack stack);

    /// <summary>
    /// Start moving part of a stack — the game's click-the-stack, click-a-bag-slot move, which
    /// asks how many in its number prompt. False when there was nowhere to put it.
    /// </summary>
    bool WithdrawSome(ChestStack stack, int amount);

    /// <summary>The "how many?" prompt (<c>InputNumeric</c>) is up.</summary>
    bool QuantityPromptOpen { get; }

    /// <summary>Answer the prompt: OK with this many.</summary>
    void AnswerQuantity(int amount);

    /// <summary>
    /// How many of the stack's item are still in its slot — 0 once it has left. The only success
    /// signal worth trusting.
    /// </summary>
    int QuantityAt(ChestStack stack);

    void Log(string message);
}

/// <summary>What one withdrawal run did.</summary>
public sealed record WithdrawReport(int Moved, int Covered, int Short, int Items = 0);

/// <summary>
/// Pulls what a quest line is missing out of the Free Company chest.
///
/// <para>
/// <b>Exactly what is short.</b> A stack that holds more than the shortfall is split the way the
/// game splits it by hand: click the stack, click a bag slot, and the "how many?" prompt
/// (<c>InputNumeric</c>; OK sends the number, Cancel <c>-1</c> — recorded 2026-09-30) is answered
/// with the shortfall. Six Copper Ingot out of a stack of 99 brings six, and the 93 stay for the
/// rest of the fleet. Stacks that do not cover it alone are taken whole, by <c>MoveItemSlot</c>,
/// which asks nothing.
/// </para>
///
/// <para>
/// <b>Verified by the slot emptying (or shrinking), never by the return code.</b> Charon established the hard way
/// that <c>MoveItemSlot</c> returns 6 on moves that demonstrably succeeded, and that the slot
/// clears noticeably later than the call — it is a server round trip. So each move is submitted,
/// then watched, one at a time.
/// </para>
///
/// <para>
/// Only pages the game has loaded can be read at all, which means pages whose tab has been viewed
/// this session. An item sitting on an unviewed page is invisible and reported as not found, never
/// as absent.
/// </para>
/// </summary>
public sealed class ChestWithdrawer
{
    /// <summary>Server-friendly pacing, and long enough that a move is usually settled by the next beat.</summary>
    private static readonly TimeSpan MoveGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MoveTimeout = TimeSpan.FromSeconds(3);

    private readonly IChestWorld _world;

    private readonly Queue<(uint ItemId, int Wanted)> _queue = new();
    private ChestStack? _inFlight;
    /// <summary>How many of <see cref="_inFlight"/> were asked for — less than its quantity for a split.</summary>
    private int _inFlightAmount;
    private bool _promptAnswered;
    private int _items;
    private DateTime _lastMove;
    private DateTime _inFlightSince;
    private int _moved;
    private int _covered;
    private int _short;

    public ChestWithdrawer(IChestWorld world) => _world = world;

    public bool Busy => _queue.Count > 0 || _inFlight is not null;

    public string Status { get; private set; } = "idle";

    /// <summary>The last finished run, for the window to show.</summary>
    public WithdrawReport? Last { get; private set; }

    /// <summary>
    /// Queue what is missing. <paramref name="needs"/> is item id and how many more are wanted —
    /// the bill's own shortfall. Returns how many lines were taken; zero means nothing to do.
    /// </summary>
    public int Start(IReadOnlyList<(uint ItemId, int Missing)> needs)
    {
        if (Busy)
            return 0;

        _queue.Clear();
        _moved = 0;
        _items = 0;
        _covered = 0;
        _short = 0;
        Last = null;

        // Queued as how many to hold when done: what is in the bags now plus the shortfall. The
        // shortfall alone, compared with the bags, fetched too few whenever some were already held
        // (2 held, 3 short: it stopped at 3 held) — hidden while whole stacks overshot.
        foreach (var (itemId, missing) in needs)
            if (missing > 0)
                _queue.Enqueue((itemId, _world.Held(itemId) + missing));

        if (_queue.Count == 0)
        {
            Status = "nothing missing";
            Last = new WithdrawReport(0, 0, 0);
            return 0;
        }

        if (!_world.ChestOpen)
        {
            _queue.Clear();
            Status = "the FC chest is not open";
            return 0;
        }

        Status = $"{_queue.Count} {(_queue.Count == 1 ? "kind" : "kinds")} of item to fetch";
        return _queue.Count;
    }

    public void Tick()
    {
        if (!Busy)
            return;

        // The window is the session. Losing it mid-run abandons cleanly rather than firing moves
        // into a closed transfer.
        if (!_world.ChestOpen)
        {
            _queue.Clear();
            _inFlight = null;
            Finish("stopped — the FC chest closed");
            return;
        }

        var now = _world.UtcNow;
        if (now - _lastMove < MoveGap)
            return;
        _lastMove = now;

        Step(now);

        // Completion is checked here rather than on a later tick: the run stops being Busy the
        // moment the queue drains, so anything left until "next time" would never happen.
        if (_queue.Count == 0 && _inFlight is null)
            Finish(null);
    }

    private void Step(DateTime now)
    {
        if (_inFlight is { } flying)
        {
            // A split waits on the game's "how many?" prompt before anything moves.
            if (_inFlightAmount < flying.Quantity && !_promptAnswered)
            {
                if (_world.QuantityPromptOpen)
                {
                    _world.AnswerQuantity(_inFlightAmount);
                    _promptAnswered = true;
                    _inFlightSince = now;
                    return;
                }
                if (now - _inFlightSince <= MoveTimeout)
                    return;
                _world.Log($"Item {flying.ItemId}: the chest never asked how many — giving up on that stack.");
                _short++;
                _queue.Dequeue();
                _inFlight = null;
                return;
            }

            if (_world.QuantityAt(flying) > flying.Quantity - _inFlightAmount)
            {
                if (now - _inFlightSince <= MoveTimeout)
                    return; // still settling — a chest move is a server round trip
                _world.Log($"Item {flying.ItemId} did not leave the chest — giving up on that stack.");
                _short++;
                _queue.Dequeue();
                _inFlight = null;
                return;
            }
            _moved++;
            _items += _inFlightAmount;
            _inFlight = null;
            return;
        }

        if (_queue.Count == 0)
            return;

        var (itemId, wanted) = _queue.Peek();
        if (_world.Held(itemId) >= wanted)
        {
            _queue.Dequeue();
            _covered++;
            return;
        }

        var stacks = _world.ChestStacks(itemId);
        if (stacks.Count == 0)
        {
            // Not on any loaded page. That is not the same as "not in the chest" — an unviewed
            // page is unreadable — so the wording never claims the chest does not have it.
            _queue.Dequeue();
            _short++;
            _world.Log($"Item {itemId}: none on the loaded chest pages.");
            return;
        }

        // Smallest stack that still covers the shortfall (split down to exactly that), else the
        // largest available, taken whole.
        ChestStack? pick = null;
        var missing = wanted - _world.Held(itemId);
        foreach (var stack in stacks)
        {
            if (stack.Quantity >= missing)
            {
                if (pick is null || pick.Quantity < missing || stack.Quantity < pick.Quantity)
                    pick = stack;
            }
            else if (pick is null || (pick.Quantity < missing && stack.Quantity > pick.Quantity))
            {
                pick = stack;
            }
        }

        var amount = pick is null ? 0 : Math.Min(pick.Quantity, missing);
        if (pick is null || !(amount < pick.Quantity ? _world.WithdrawSome(pick, amount) : _world.Withdraw(pick)))
        {
            _queue.Dequeue();
            _short++;
            _world.Log($"Item {itemId}: could not be withdrawn — bags full?");
            return;
        }

        Status = $"fetching {amount} × item {itemId}";
        _inFlight = pick;
        _inFlightAmount = amount;
        _promptAnswered = false;
        _inFlightSince = now;
    }

    /// <param name="abandoned">Why it stopped early, or null when it simply finished.</param>
    private void Finish(string? abandoned)
    {
        Last = new WithdrawReport(_moved, _covered, _short, _items);
        Status = abandoned ?? (_short > 0
            ? $"brought {_items} item(s); {_short} kind(s) not found on a loaded page"
            : $"brought {_items} item(s)");
        _inFlight = null;
    }
}
