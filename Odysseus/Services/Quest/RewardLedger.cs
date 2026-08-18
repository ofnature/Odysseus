using System;
using System.Collections.Generic;

namespace Odysseus.Services.Quest;

/// <summary>One thing waiting to be sold, and how many of it.</summary>
public sealed record PendingSale(uint ItemId, int Quantity);

/// <summary>
/// What the quests we ran actually gave us, and therefore what may be sold.
///
/// <para>
/// <b>Measured, never assumed.</b> The bag is counted before a quest is handed in and again after,
/// and only the <i>increase</i> is banked. That is the whole safety property: the sheet lists four
/// optional rewards of which one arrives, so trusting it would mark three items that never came —
/// and one of those could be a stack you were keeping. A difference of zero banks nothing, so an
/// item you already owned is never touched no matter what the sheet says about it.
/// </para>
///
/// <para>
/// Quantities are banked, not item ids: selling is capped at what the quests handed over, so a
/// reward of one Venture can never take the two hundred behind it.
/// </para>
///
/// <para>Pure — the caller supplies the counts, which is what makes all of this testable.</para>
/// </summary>
public sealed class RewardLedger
{
    private readonly Dictionary<uint, int> _pending = new();
    private readonly Dictionary<uint, int> _before = new();
    private ushort _measuring;

    /// <param name="pending">Restored from config, so a hand-in survives a restart on the way to the vendor.</param>
    public RewardLedger(IEnumerable<PendingSale>? pending = null)
    {
        if (pending is null) return;
        foreach (var sale in pending)
            if (sale.Quantity > 0)
                _pending[sale.ItemId] = _pending.GetValueOrDefault(sale.ItemId) + sale.Quantity;
    }

    /// <summary>Everything banked and still unsold.</summary>
    public IReadOnlyList<PendingSale> Pending
    {
        get
        {
            var list = new List<PendingSale>(_pending.Count);
            foreach (var (itemId, quantity) in _pending)
                if (quantity > 0)
                    list.Add(new PendingSale(itemId, quantity));
            return list;
        }
    }

    public bool Any
    {
        get
        {
            foreach (var quantity in _pending.Values)
                if (quantity > 0) return true;
            return false;
        }
    }

    /// <summary>
    /// Count the candidates before the hand-in. Called when a quest is about to complete; a second
    /// call for a different quest replaces the first, because only one hand-in is ever in flight.
    /// </summary>
    public void Before(ushort questId, IReadOnlyList<uint> candidates, Func<uint, int> held)
    {
        _measuring = questId;
        _before.Clear();
        foreach (var itemId in candidates)
            _before[itemId] = held(itemId);
    }

    /// <summary>
    /// Count them again and bank what arrived. Returns just what this hand-in added, for the log.
    /// A quest that was not measured banks nothing — the alternative is treating a whole bag as a
    /// reward because the before-count was never taken.
    /// </summary>
    public IReadOnlyList<PendingSale> After(ushort questId, Func<uint, int> held)
    {
        if (_measuring != questId || _before.Count == 0)
            return [];

        var gained = new List<PendingSale>();
        foreach (var (itemId, was) in _before)
        {
            var now = held(itemId);
            if (now <= was) continue;
            var delta = now - was;
            _pending[itemId] = _pending.GetValueOrDefault(itemId) + delta;
            gained.Add(new PendingSale(itemId, delta));
        }
        _before.Clear();
        _measuring = 0;
        return gained;
    }

    /// <summary>How many of an item are owed to the vendor, capped at what is actually in the bag.</summary>
    public int Owed(uint itemId, int held) => Math.Min(_pending.GetValueOrDefault(itemId), Math.Max(0, held));

    /// <summary>Bank a sale. Selling more than was owed still clears only what was owed.</summary>
    public void Sold(uint itemId, int quantity)
    {
        if (quantity <= 0 || !_pending.TryGetValue(itemId, out var owed)) return;
        var left = owed - quantity;
        if (left > 0) _pending[itemId] = left;
        else _pending.Remove(itemId);
    }

    /// <summary>Give up on an item — it could not be sold and retrying it forever helps nobody.</summary>
    public void Forget(uint itemId) => _pending.Remove(itemId);

    public void Clear()
    {
        _pending.Clear();
        _before.Clear();
        _measuring = 0;
    }
}
