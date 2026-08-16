using System;
using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Deliveries;

public enum SpendRunState
{
    Idle,
    /// <summary>Opening the scrip vendor's window.</summary>
    Open,
    /// <summary>Buying, one unit at a time.</summary>
    Buy,
    Done,
    /// <summary>Stopped on purpose — the reason is in <see cref="SpendRunner.StatusLine"/>.</summary>
    Blocked,
    Faulted,
}

/// <summary>
/// Spends a <see cref="SpendPlan"/> at a scrip vendor.
///
/// <para>
/// <b>Every purchase is verified before the next one.</b> A special shop has no agent to drive —
/// ClientStructs models gil shops but not these — so buying goes through the
/// <c>ShopExchangeCurrency</c> addon's own callback, and that shape cannot be checked without the
/// game in front of you. So the run buys a single unit, confirms the item actually arrived, and
/// only then buys again. A wrong callback costs one purchase and stops, instead of emptying the
/// balance into the wrong row.
/// </para>
///
/// <para>
/// It does not travel. Scrip vendors stand in every major city and the plan is small, so requiring
/// you to be at one keeps this to the part that needed building.
/// </para>
/// </summary>
public sealed class SpendRunner
{
    private static readonly TimeSpan BuyGap = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OpenStall = TimeSpan.FromSeconds(15);
    /// <summary>How long a bought item may take to appear before the purchase is called a failure.</summary>
    private static readonly TimeSpan ArriveWait = TimeSpan.FromSeconds(5);

    private readonly IDeliveryWorld _game;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;

    private Queue<SpendLine> _queue = new();
    private SpendLine? _line;
    private int _boughtOfLine;
    private int _countBefore = -1;
    private DateTime _phaseStart;
    private DateTime _lastAction;
    private uint _vendor;
    private uint _shop;

    public SpendRunner(IDeliveryWorld game, Action<string> log, Func<DateTime>? now = null)
    {
        _game = game;
        _log = log;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public SpendRunState State { get; private set; } = SpendRunState.Idle;
    public string StatusLine { get; private set; } = string.Empty;
    public int Bought { get; private set; }
    public bool IsFinished => State is SpendRunState.Idle or SpendRunState.Done
        or SpendRunState.Blocked or SpendRunState.Faulted;

    /// <summary>Begin spending. False with a reason in <see cref="StatusLine"/>.</summary>
    public bool Start(SpendPlan plan)
    {
        if (plan.IsEmpty)
        {
            StatusLine = plan.Summary;
            return false;
        }

        // Everything in one plan has to come from one window; take the first shop and keep to it.
        _shop = plan.Lines[0].Offer.ShopId;
        var lines = plan.Lines.Where(l => l.Offer.ShopId == _shop).ToList();
        if (_shop == 0)
        {
            StatusLine = "The scrip vendor for these items could not be identified.";
            return false;
        }

        _vendor = _game.FindSpecialShopVendor(_shop);
        if (_vendor == 0 && !_game.IsSpecialShopOpen)
        {
            StatusLine = "No scrip vendor nearby. Stand next to one and press Spend again — " +
                         "Odysseus does not travel to them.";
            return false;
        }

        _queue = new Queue<SpendLine>(lines);
        _line = null;
        _boughtOfLine = 0;
        _countBefore = -1;
        Bought = 0;
        if (lines.Count < plan.Lines.Count)
            _log($"Spending {lines.Count} of {plan.Lines.Count} lines — the rest are at a different vendor.");
        Enter(SpendRunState.Open);
        return true;
    }

    public void Stop()
    {
        _game.CloseSpecialShop();
        State = SpendRunState.Idle;
    }

    public void Tick()
    {
        if (IsFinished) return;
        try
        {
            switch (State)
            {
                case SpendRunState.Open: TickOpen(); break;
                case SpendRunState.Buy: TickBuy(); break;
            }
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TickOpen()
    {
        if (_game.IsSpecialShopOpen)
        {
            Enter(SpendRunState.Buy);
            return;
        }

        StatusLine = "Opening the scrip vendor";
        if (_now() - _lastAction > BuyGap)
        {
            if (_vendor != 0 && !_game.OpenSpecialShop(_vendor, _shop))
                _log("The scrip vendor would not open its exchange.");
            _lastAction = _now();
        }
        if (_now() - _phaseStart > OpenStall)
            Block("Could not open the scrip exchange window.");
    }

    private void TickBuy()
    {
        // A purchase is in flight: it counts only once the item is actually in the bag.
        if (_line is { } pending && _countBefore >= 0)
        {
            var now = _game.ItemCount(pending.Offer.ItemId);
            if (now > _countBefore)
            {
                Bought++;
                _boughtOfLine++;
                _countBefore = -1;
                _lastAction = _now();
                _phaseStart = _now();
                return;
            }
            if (_now() - _phaseStart <= ArriveWait) return;

            // Nothing arrived. Either the callback did nothing, or it bought something else —
            // either way, stop rather than repeat it.
            Block($"Bought {Bought} item(s), then {pending.Offer.Name} did not arrive. " +
                  "The scrip window did not do what was asked, so nothing more will be tried.");
            return;
        }

        if (_line is null || _boughtOfLine >= _line.Quantity)
        {
            if (_queue.Count == 0)
            {
                _game.CloseSpecialShop();
                State = SpendRunState.Done;
                StatusLine = Bought == 0 ? "Nothing was bought." : $"Bought {Bought} item(s).";
                _log(StatusLine);
                return;
            }
            _line = _queue.Dequeue();
            _boughtOfLine = 0;
        }

        if (!_game.IsSpecialShopOpen)
        {
            Block($"The scrip window closed after {Bought} item(s).");
            return;
        }

        StatusLine = $"Buying {_line.Offer.Name} ({_boughtOfLine + 1}/{_line.Quantity})";
        if (_now() - _lastAction < BuyGap) return;

        _countBefore = _game.ItemCount(_line.Offer.ItemId);
        if (!_game.BuyOneFromSpecialShop(_line.Offer.ItemId))
        {
            _countBefore = -1;
            Block($"{_line.Offer.Name} is not on this vendor's list.");
            return;
        }
        _lastAction = _now();
        _phaseStart = _now();
    }

    private void Enter(SpendRunState state)
    {
        State = state;
        _phaseStart = _now();
        _lastAction = default;
    }

    private void Block(string reason)
    {
        _game.CloseSpecialShop();
        State = SpendRunState.Blocked;
        StatusLine = reason;
        _log($"Spending stopped: {reason}");
    }

    private void Fault(string reason)
    {
        _game.CloseSpecialShop();
        State = SpendRunState.Faulted;
        StatusLine = reason;
        _log($"FAULT: {reason}");
    }
}
