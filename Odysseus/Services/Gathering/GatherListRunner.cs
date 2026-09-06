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
    private bool _repairing;

    public GatherListRunner(IOwnGatherer gatherer, Func<uint, int> held, Func<uint, string> nameOf, Action<string> log,
        GearRepair? repair = null, Func<int>? repairAt = null, Func<int>? freeSlots = null)
    {
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
        if (_gatherer.Faulted)
        {
            Finish(item, target, $"gave up: {_gatherer.Status}");
            return;
        }
        if (!_gatherer.Busy)
        {
            Finish(item, target, _held(item) >= target ? "done" : "the nodes ran out short");
            return;
        }
        Status = $"{_nameOf(item)} {_held(item)}/{target} — {_gatherer.Status}";
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
            if (!_gatherer.CanGather(item))
            {
                Record(item, target, held, $"skipped: {_gatherer.WhyNot(item)}");
                continue;
            }
            if (!_gatherer.Start(item, target - held, 0))
            {
                Record(item, target, held, "skipped: the gatherer would not start");
                continue;
            }
            _current = (item, target);
            Status = $"{name} {held}/{target}";
            _log($"Gathering {target - held} × {name} ({held}/{target}).");
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
