using System;
using System.Collections.Generic;
using System.Linq;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Services.Travel;

/// <summary>
/// "Attune this zone": every aetheryte and aethernet shard in the zone you stand in that this
/// character has not attuned, one after another. Each is one synthesised attune step through the
/// same executor as everything else, so the walk, the mount and the flight are a quest's. One that
/// cannot be reached is named at the end and the rest are still done.
/// </summary>
public sealed class AttuneRunner
{
    private readonly IStepWorld _world;
    private readonly StepExecutor _executor;
    private readonly Action<string> _log;

    /// <summary>What is left, taken nearest-first from wherever the last one left us.</summary>
    private List<(uint Id, string Name, bool IsShard)> _queue = new();
    private (uint Id, string Name, bool IsShard)? _current;
    private readonly List<string> _missed = [];
    private uint _territory;

    public AttuneRunner(IStepWorld world, StepExecutor executor, Action<string> log)
    {
        _world = world;
        _executor = executor;
        _executor.AttuneInPassing = false; // this is the attuning
        _log = log;
    }

    public bool Running { get; private set; }
    public string StatusLine { get; private set; } = string.Empty;
    public int Done { get; private set; }
    public int Target { get; private set; }
    public bool IsFinished => !Running;

    /// <summary>How many are left to attune here — for the button.</summary>
    public int MissingHere => _world.UnattunedHere().Count;

    public bool Start()
    {
        var missing = _world.UnattunedHere();
        if (missing.Count == 0)
        {
            StatusLine = "Everything in this zone is attuned.";
            return false;
        }
        _queue = new List<(uint, string, bool)>(missing);
        _current = null;
        _missed.Clear();
        _territory = _world.TerritoryId;
        Done = 0;
        Target = missing.Count;
        Running = true;
        StatusLine = $"Attuning {Target} here.";
        _log($"Attuning {Target} aetheryte(s) and shard(s) in territory {_territory}: {string.Join(", ", missing.Select(m => m.Name))}.");
        return true;
    }

    public void Stop()
    {
        _executor.Cancel();
        Running = false;
        StatusLine = "Stopped.";
    }

    public void Tick()
    {
        if (!Running) return;
        try
        {
            if (_world.TerritoryId != _territory)
            {
                Finish("Left the zone, so attuning stopped.");
                return;
            }

            if (_current is { } running)
            {
                var status = _executor.Tick();
                if (status == StepStatus.Running)
                {
                    StatusLine = $"{running.Name} ({Done + _missed.Count + 1}/{Target})";
                    return;
                }
                if (_world.IsAttuned(running.Id))
                    Done++;
                else
                    _missed.Add($"{running.Name}{(status == StepStatus.Failed ? $" ({_executor.FailReason})" : string.Empty)}");
                _current = null;
                return;
            }

            if (_queue.Count == 0)
            {
                Finish(_missed.Count == 0
                    ? $"Attuned {Done}."
                    : $"Attuned {Done}; could not attune {string.Join("; ", _missed)}.");
                return;
            }

            // Shards first, then the zone's aetheryte; each nearest first, chosen afresh after each.
            // Distance is across the ground: a map marker has no height, and the guess borrows ours,
            // so Eulmore's aetheryte three floors up measured as if on our floor and, standing near the
            // middle of the city, came out "nearest" — newtoon2 climbed to the Canopy first and walked
            // back down for the shards. A city's own aetheryte is also the one the story locks (Eulmore's
            // until Paradise Fallen), so it is the right one to leave for last. Unplaceable ones go last.
            var here = new System.Numerics.Vector2(_world.PlayerPosition.X, _world.PlayerPosition.Z);
            var next = _queue
                .OrderBy(q => q.IsShard ? 0 : 1)
                .ThenBy(q => _world.AttunableAt(q.Id) is { } place
                    ? System.Numerics.Vector2.Distance(here, new System.Numerics.Vector2(place.At.X, place.At.Z))
                    : float.MaxValue)
                .First();
            _queue.Remove(next);
            _current = next;
            _executor.Begin(new QuestStep
            {
                Kind = next.IsShard ? StepKind.AttuneAethernetShard : StepKind.AttuneAetheryte,
                KindName = next.IsShard ? nameof(StepKind.AttuneAethernetShard) : nameof(StepKind.AttuneAetheryte),
                TerritoryId = _territory,
                AttuneId = next.Id,
                AttuneName = next.Name,
            });
        }
        catch (Exception ex)
        {
            _executor.Cancel();
            Finish($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Finish(string line)
    {
        _executor.Cancel();
        Running = false;
        StatusLine = line;
        _log(line);
    }
}
