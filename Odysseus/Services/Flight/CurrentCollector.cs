using System.Numerics;
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
    /// <summary>Currents that would not attune or could not be reached; named at the end, the rest still collected.</summary>
    private readonly List<string> _missed = [];
    private int _later;
    private int _collected;
    private uint _territory;

    public CurrentCollector(IStepWorld world, IFlightState state, StepExecutor executor, Action<string> log)
    {
        _world = world;
        _state = state;
        _executor = executor;
        _log = log;
    }

    /// <summary>
    /// Whether the story has taken this character near a spot yet (territory, position). Shadowbringers
    /// zones are split into a side the story opens early and one it opens late; a current on the late
    /// side cannot be reached, and every one tried cost three failed paths. Null: try them all.
    /// </summary>
    public Func<uint, Vector3, bool>? StoryHasBeen { get; set; }

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
        _missed.Clear();
        var placed = missing.Where(c => c.Position is not null).ToList();
        var later = placed.Where(c => StoryHasBeen?.Invoke(zone.TerritoryId, c.Position!.Value) == false).ToList();
        var reachable = placed.Except(later).ToList();
        _later = later.Count;

        if (reachable.Count == 0)
        {
            StatusLine = later.Count > 0
                ? $"{later.Count} current(s) left in {zone.Name}, on ground the story has not opened yet."
                : _unknown.Count > 0
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
        _log($"{zone.Name}: collecting {Target} aether current(s)"
             + (later.Count > 0 ? $"; {later.Count} more are on ground the story has not opened yet." : "."));
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
            // One that will not come is noted and passed: the rest are still worth the trip.
            if (status == StepStatus.Failed)
            {
                _missed.Add($"{running.Id} (could not reach: {_executor.FailReason})");
                _log($"Current {running.Id}: could not reach it — {_executor.FailReason}. Going on to the next.");
                _executor.Cancel();
                _current = null;
                return;
            }
            if (status == StepStatus.Done && !_state.IsUnlocked(running.Id))
            {
                _missed.Add($"{running.Id} (reached, did not attune)");
                _log($"Current {running.Id}: reached but it did not attune. Going on to the next.");
                _executor.Cancel();
                _current = null;
                return;
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
            StatusLine = $"Collected {_collected} of {Target} aether current(s)."
                + (_missed.Count > 0 ? $" Missed: {string.Join("; ", _missed)}." : "")
                + (_later > 0 ? $" {_later} left for later — the story has not opened that ground." : "")
                + (_unknown.Count > 0 ? $" {_unknown.Count} more exist but no path recorded where." : "");
            _log(StatusLine);
            return;
        }

        _current = _queue.Dequeue();
        _executor.Begin(new QuestStep
        {
            Kind = StepKind.AttuneAetherCurrent,
            KindName = nameof(StepKind.AttuneAetherCurrent),
            Position = _current.Position,
            DataId = _current.DataId,
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
