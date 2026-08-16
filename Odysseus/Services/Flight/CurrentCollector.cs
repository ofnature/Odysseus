using System;
using System.Collections.Generic;
using System.Linq;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Services.Flight;

public enum CollectState
{
    Idle,
    /// <summary>Walking to the next current and attuning it.</summary>
    Collecting,
    Done,
    /// <summary>Stopped on purpose — the reason is in <see cref="CurrentCollector.StatusLine"/>.</summary>
    Blocked,
    Faulted,
}

/// <summary>
/// Picks up the loose aether currents in the zone you are standing in.
///
/// <para>
/// Only the ones lying in the world: the rest come from quests, and a quest is the quest engine's
/// job, not this one's — the window queues those onto the priority list instead. Splitting it that
/// way means neither half has to know about the other.
/// </para>
///
/// <para>
/// Each current is one synthesised <c>AttuneAetherCurrent</c> step, run through the same executor
/// everything else uses, so travel, mounting and flight behave exactly as they do in a quest. A
/// current whose position no path ever recorded is skipped and named at the end rather than
/// guessed at.
/// </para>
/// </summary>
public sealed class CurrentCollector
{
    private readonly IStepWorld _world;
    private readonly IFlightState _state;
    private readonly StepExecutor _executor;
    private readonly Action<string> _log;

    private Queue<AetherCurrent> _queue = new();
    private AetherCurrent? _current;
    private List<uint> _unknown = [];
    private int _collected;
    private uint _territory;

    public CurrentCollector(IStepWorld world, IFlightState state, StepExecutor executor, Action<string> log)
    {
        _world = world;
        _state = state;
        _executor = executor;
        _log = log;
    }

    public CollectState State { get; private set; } = CollectState.Idle;
    public string StatusLine { get; private set; } = string.Empty;
    public int Collected => _collected;
    public int Target { get; private set; }
    public bool IsFinished => State is CollectState.Idle or CollectState.Done
        or CollectState.Blocked or CollectState.Faulted;

    /// <summary>Begin collecting this zone's loose currents. False with a reason in <see cref="StatusLine"/>.</summary>
    public bool Start(ZoneFlight zone)
    {
        if (_world.TerritoryId != zone.TerritoryId)
        {
            StatusLine = $"You are not in {zone.Name}. Travel there and start it again — " +
                         "collecting does not teleport between zones.";
            return false;
        }

        var missing = zone.Currents.Where(c => !c.FromQuest && !_state.IsUnlocked(c.Id)).ToList();
        _unknown = missing.Where(c => c.Position is null).Select(c => c.Id).ToList();
        var reachable = missing.Where(c => c.Position is not null).ToList();

        if (reachable.Count == 0)
        {
            StatusLine = _unknown.Count > 0
                ? $"{_unknown.Count} current(s) left in {zone.Name}, but no path ever recorded where they are."
                : $"Nothing loose left to collect in {zone.Name}.";
            return false;
        }

        _queue = new Queue<AetherCurrent>(reachable);
        _current = null;
        _collected = 0;
        Target = reachable.Count;
        _territory = zone.TerritoryId;
        State = CollectState.Collecting;
        _log($"{zone.Name}: collecting {Target} aether current(s).");
        return true;
    }

    public void Stop()
    {
        _executor.Cancel();
        State = CollectState.Idle;
    }

    public void Tick()
    {
        if (IsFinished) return;
        try
        {
            TickCollect();
        }
        catch (Exception ex)
        {
            _executor.Cancel();
            State = CollectState.Faulted;
            StatusLine = $"{ex.GetType().Name}: {ex.Message}";
            _log($"FAULT: {StatusLine}");
        }
    }

    private void TickCollect()
    {
        if (_world.TerritoryId != _territory)
        {
            Block("Left the zone, so collecting stopped.");
            return;
        }

        if (_current is { } running)
        {
            // The game is the authority on whether it worked, not the executor: an attune that
            // silently failed leaves the current locked, and repeating it would spin forever.
            if (_state.IsUnlocked(running.Id))
            {
                _collected++;
                _executor.Cancel();
                _current = null;
                return;
            }

            StatusLine = $"Collecting current {_collected + 1}/{Target}";
            var status = _executor.Tick();
            if (status == StepStatus.Failed)
            {
                Block($"Could not reach current {running.Id} — {_executor.FailReason}. " +
                      $"{_collected} of {Target} collected.");
                return;
            }
            if (status == StepStatus.Done && !_state.IsUnlocked(running.Id))
            {
                Block($"Reached current {running.Id} but it did not attune. {_collected} of {Target} collected.");
                return;
            }
            return;
        }

        // Skip anything picked up since we started — a quest may have granted it meanwhile.
        while (_queue.Count > 0 && _state.IsUnlocked(_queue.Peek().Id))
        {
            _queue.Dequeue();
            _collected++;
        }

        if (_queue.Count == 0)
        {
            State = CollectState.Done;
            StatusLine = _unknown.Count > 0
                ? $"Collected {_collected}. {_unknown.Count} more exist but no path recorded where."
                : $"Collected {_collected} aether current(s).";
            _log(StatusLine);
            return;
        }

        _current = _queue.Dequeue();
        _executor.Begin(new QuestStep
        {
            Kind = StepKind.AttuneAetherCurrent,
            KindName = nameof(StepKind.AttuneAetherCurrent),
            Position = _current.Position,
            TerritoryId = _territory,
            AetherCurrentId = _current.Id,
            Fly = true,
        });
    }

    private void Block(string reason)
    {
        _executor.Cancel();
        State = CollectState.Blocked;
        StatusLine = reason;
        _log($"Collecting stopped: {reason}");
    }
}
