using System;
using System.Collections.Generic;
using System.Linq;
using Odysseus.Services.Run;

namespace Odysseus.Services.Gathering;

public enum GatherListRunState { Idle, Running, Done }

/// <summary>What became of one item of a run.</summary>
public sealed record GatherOutcome(uint ItemId, string Name, int Target, int Held, string Note)
{
    public bool Reached => Held >= Target;
}

/// <summary>
/// Runs gather lists: everything short across the enabled lists, one item at a time through
/// the own gatherer, ordered by zone so the teleports are paid once per zone rather than once
/// per item. An item that cannot be placed or that faults is recorded and the next is tried —
/// one bad row does not end the run.
/// </summary>
public sealed class GatherListRunner
{
    private readonly IOwnGatherer _gatherer;
    private readonly Func<uint, int> _held;
    private readonly Func<uint, string> _nameOf;
    private readonly Action<string> _log;
    private readonly Queue<(uint ItemId, int Target)> _queue = new();
    private readonly List<GatherOutcome> _outcomes = new();
    private (uint ItemId, int Target)? _current;
    private readonly GearRepair? _repair;
    private readonly Func<int> _repairAt;
    private readonly Func<int> _freeSlots;
    private readonly Func<DateTime> _now;
    private bool _repairing;

    /// <summary>Windows a timed item is tried in before the run gives up on it.</summary>
    private const int MaxTimedWindows = 3;
    private readonly Dictionary<uint, int> _timedTries = new();

    /// <summary>The item in hand is one of the timed ones.</summary>
    private bool _currentTimed;

    /// <summary>
    /// Timed items already worked in their current window, until it ends. Kept across runs: a node
    /// gathered, then Stop and Gather again in the same window, chained round all three empty spots.
    /// </summary>
    private readonly Dictionary<uint, DateTime> _workedUntil = new();

    /// <summary>The wait before a timed item can be gathered — its window, and not the one already worked.</summary>
    private TimeSpan? WaitOf(uint item, DateTime now)
    {
        if (_workedUntil.TryGetValue(item, out var until) && until > now)
        {
            var after = until.AddSeconds(1);
            return _gatherer.WaitFor(item, after) is { } next ? after - now + next : null;
        }
        return _gatherer.WaitFor(item, now);
    }

    /// <summary>A timed node came up mid-item: the node in hand is being finished before going.</summary>
    private bool _yielding;

    public GatherListRunner(IOwnGatherer gatherer, Func<uint, int> held, Func<uint, string> nameOf, Action<string> log,
        GearRepair? repair = null, Func<int>? repairAt = null, Func<int>? freeSlots = null, Func<DateTime>? now = null)
    {
        _now = now ?? (() => DateTime.UtcNow);
        _gatherer = gatherer;
        _held = held;
        _nameOf = nameOf;
        _log = log;
        _repair = repair;
        _repairAt = repairAt ?? (() => 0);
        _freeSlots = freeSlots ?? (() => int.MaxValue);
    }

    public GatherListRunState State { get; private set; } = GatherListRunState.Idle;
    public string Status { get; private set; } = string.Empty;
    public IReadOnlyList<GatherOutcome> Outcomes => _outcomes;
    public uint? CurrentItem => _current?.ItemId;
    public int Remaining => _queue.Count + (_current is null ? 0 : 1);

    /// <summary>Snapshot the short items of the enabled lists and start. False when nothing is short.</summary>
    public bool Begin(IEnumerable<GatherList> lists)
    {
        // One entry per item at the highest target any list asks for.
        var wanted = new Dictionary<uint, int>();
        foreach (var list in lists)
        {
            if (!list.Enabled)
                continue;
            foreach (var item in list.Items)
                if (item.TargetCount > 0)
                    wanted[item.ItemId] = Math.Max(wanted.GetValueOrDefault(item.ItemId), item.TargetCount);
        }

        var shortItems = wanted.Where(kv => _held(kv.Key) < kv.Value).ToList();
        if (shortItems.Count == 0)
        {
            Status = "Nothing on the enabled lists is short.";
            return false;
        }

        _queue.Clear();
        _outcomes.Clear();
        _timedTries.Clear();
        _current = null;
        // Zone order, unknown zones last; stable within a zone so the list's own order holds.
        foreach (var kv in shortItems.OrderBy(kv => _gatherer.ZoneOf(kv.Key) ?? uint.MaxValue))
            _queue.Enqueue((kv.Key, kv.Value));
        State = GatherListRunState.Running;
        Status = $"{_queue.Count} item(s) short.";
        _log($"Gather lists: {_queue.Count} item(s) short.");
        return true;
    }

    public void Stop()
    {
        if (_repairing)
        {
            _repair?.Cancel();
            _repairing = false;
        }
        if (_current is not null)
            _gatherer.Stop();
        _queue.Clear();
        _current = null;
        State = GatherListRunState.Idle;
        Status = "Stopped.";
    }

    public void Tick()
    {
        if (State != GatherListRunState.Running)
            return;

        // A full bag ends the run honestly instead of forty stops of failed gathers.
        if (_freeSlots() <= 0)
        {
            if (_current is { } stuck)
            {
                _gatherer.Stop();
                Record(stuck.ItemId, stuck.Target, _held(stuck.ItemId), "stopped: the bag is full");
                _current = null;
            }
            _queue.Clear();
            State = GatherListRunState.Done;
            Status = "Stopped — the bag is full.";
            _log($"Gather lists: {Status}");
            return;
        }

        if (_repairing)
        {
            _repair!.Tick();
            if (!_repair.Busy)
            {
                _repairing = false;
                if (_repair.State == RepairState.Faulted)
                    Status = $"Repair gave up: {_repair.FailReason} — carrying on.";
            }
            return;
        }

        if (_current is null)
        {
            StartNext();
            return;
        }

        _gatherer.Tick();
        var (item, target) = _current.Value;

        // A timed node has opened while an ordinary item is in hand: finish the node being worked,
        // go for the timed one while it is up, then come back — the ordinary node will still be there.
        if (!_currentTimed && _gatherer.Busy && !_gatherer.Faulted && ReadyTimed() is { } timed)
        {
            if (!_yielding)
            {
                _yielding = true;
                _log($"{_nameOf(timed)}'s node is up — {(_gatherer.AtNode ? "finishing this node, then " : string.Empty)}going for it.");
            }
            if (!_gatherer.AtNode)
            {
                _gatherer.Stop();
                _yielding = false;
                _current = null;
                PutFirst(timed, then: (item, target));
                Status = $"{_nameOf(timed)}'s node is up — going for it; back to {_nameOf(item)} after.";
                return;
            }
        }
        else
        {
            _yielding = false;
        }
        if (_gatherer.Faulted || !_gatherer.Busy)
        {
            var short_ = _held(item) < target;
            // This window's node is spent, whatever came of it.
            if (_currentTimed && _gatherer.UpFor(item, _now()) is { } left)
                _workedUntil[item] = _now() + left;
            // A timed node's window can close mid-run; the next one is another chance, a few times.
            if (short_ && WaitOf(item, _now()) is not null
                && _timedTries.GetValueOrDefault(item) < MaxTimedWindows)
            {
                _log($"{_nameOf(item)}: {_held(item)}/{target} when its window ended — back in the queue for the next one.");
                _queue.Enqueue((item, target));
                _current = null;
                return;
            }
            Finish(item, target, _gatherer.Faulted ? $"gave up: {_gatherer.Status}" : short_ ? "the nodes ran out short" : "done");
            return;
        }
        Status = $"{_nameOf(item)} {_held(item)}/{target} — {_gatherer.Status}";
    }

    /// <summary>A queued timed item whose node can be gathered right now, or null.</summary>
    private uint? ReadyTimed()
    {
        var now = _now();
        foreach (var (queued, _) in _queue)
            if (WaitOf(queued, now) == TimeSpan.Zero)
                return queued;
        return null;
    }

    /// <summary>Reorder the queue: the timed item first, the interrupted one straight after it, the rest as they were.</summary>
    private void PutFirst(uint timed, (uint ItemId, int Target) then)
    {
        var rest = _queue.ToList();
        _queue.Clear();
        foreach (var entry in rest.Where(e => e.ItemId == timed)) _queue.Enqueue(entry);
        _queue.Enqueue(then);
        foreach (var entry in rest.Where(e => e.ItemId != timed)) _queue.Enqueue(entry);
    }

    private void StartNext()
    {
        // Between items is where a repair fits: no node open, nothing mid-flight.
        if (_queue.Count > 0 && _repair is not null && _repair.Needed(_repairAt()))
        {
            _repairing = true;
            Status = "Repairing gear.";
            _repair.Begin();
            return;
        }

        // Timed items whose node is not up yet: set aside while the rest is gathered, then waited on.
        var waiting = new List<(uint ItemId, int Target, TimeSpan Wait)>();
        var now = _now();

        // One that is up goes first, whatever its place in the queue: its window will not wait.
        if (ReadyTimed() is { } ready && _queue.Peek().ItemId != ready)
        {
            var rest = _queue.ToList();
            _queue.Clear();
            foreach (var entry in rest.Where(e => e.ItemId == ready)) _queue.Enqueue(entry);
            foreach (var entry in rest.Where(e => e.ItemId != ready)) _queue.Enqueue(entry);
        }
        while (_queue.Count > 0)
        {
            var (item, target) = _queue.Dequeue();
            var held = _held(item);
            var name = _nameOf(item);
            if (held >= target)
            {
                Record(item, target, held, "already held");
                continue;
            }
            var wait = WaitOf(item, now);
            if (wait is { } w && w > TimeSpan.Zero)
            {
                waiting.Add((item, target, w));
                continue;
            }
            if (wait is null && !_gatherer.CanGather(item))
            {
                Record(item, target, held, $"skipped: {_gatherer.WhyNot(item)}");
                continue;
            }
            if (wait is not null)
                _timedTries[item] = _timedTries.GetValueOrDefault(item) + 1;
            _currentTimed = wait is not null;
            if (!_gatherer.Start(item, target - held, 0))
            {
                Record(item, target, held, "skipped: the gatherer would not start");
                continue;
            }
            _current = (item, target);
            Status = $"{name} {held}/{target}";
            _log($"Gathering {target - held} × {name} ({held}/{target}){(wait is not null ? " — its timed node is up" : string.Empty)}.");
            foreach (var (waitItem, waitTarget, _) in waiting) _queue.Enqueue((waitItem, waitTarget));
            return;
        }

        if (waiting.Count > 0)
        {
            foreach (var (waitItem, waitTarget, _) in waiting) _queue.Enqueue((waitItem, waitTarget));
            var soonest = waiting.MinBy(x => x.Wait);
            var span = soonest.Wait;
            Status = $"Waiting for {_nameOf(soonest.ItemId)} — its node is up in " +
                     (span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}");
            return;
        }

        State = GatherListRunState.Done;
        var reached = _outcomes.Count(o => o.Reached);
        Status = $"Done — {reached} of {_outcomes.Count} item(s) reached their target.";
        _log($"Gather lists: {Status}");
    }

    private void Finish(uint item, int target, string note)
    {
        Record(item, target, _held(item), note);
        _current = null;
    }

    private void Record(uint item, int target, int held, string note)
    {
        _outcomes.Add(new GatherOutcome(item, _nameOf(item), target, held, note));
        if (note != "done")
            _log($"{_nameOf(item)}: {note} ({held}/{target}).");
    }
}
