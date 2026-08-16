using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Services.Tribes;

public enum TribeRunState
{
    Idle,
    /// <summary>Switching to a combat gearset the tribe's dailies need.</summary>
    Job,
    /// <summary>Travelling to the issuer.</summary>
    Travel,
    /// <summary>Clicking through the issuer's dialogue to accept dailies.</summary>
    Accept,
    /// <summary>Running an accepted daily through the quest controller.</summary>
    Run,
    Done,
    Faulted,
}

/// <summary>
/// Runs one allied society's dailies for the day: get the right job, go to the issuer, accept up
/// to the day's slots, then run each accepted daily to completion through the same
/// <see cref="QuestController"/> the MSQ uses.
///
/// <para>
/// The accept step is the one piece the quest engine cannot do — there is no path for "talk to the
/// issuer and pick three dailies" — so it is an addon loop here: interact, then answer whatever
/// window comes up (icon-list of offers, a select-string, the journal-accept confirm, talk
/// advances) until the accepted count reaches the target. Modelled on Auto Daily Tribes, but the
/// running is ours, not delegated.
/// </para>
/// </summary>
public sealed class TribeRunner
{
    private static readonly TimeSpan AcceptStall = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan JobStall = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReinteractGap = TimeSpan.FromSeconds(3);

    private readonly IStepWorld _world;
    private readonly ITribeState _state;
    private readonly QuestController _controller;
    private readonly StepExecutor _travelExecutor;
    private readonly Action<string> _log;

    private TribeInfo? _tribe;
    private TribeIssuer? _issuer;
    private DateTime _phaseStart;
    private DateTime _lastInteract;
    private int _acceptTarget;
    private Queue<ushort> _toRun = new();
    private ushort _running;

    public TribeRunner(IStepWorld world, ITribeState state, QuestController controller, StepExecutor travelExecutor, Action<string> log)
    {
        _world = world;
        _state = state;
        _controller = controller;
        _travelExecutor = travelExecutor;
        _log = log;
    }

    public TribeRunState State { get; private set; } = TribeRunState.Idle;
    public string StatusLine { get; private set; } = string.Empty;
    public byte TribeId => _tribe?.Id ?? 0;

    /// <summary>Begin the tribe's dailies. Returns false with a reason when it cannot start.</summary>
    public bool Start(TribeInfo tribe)
    {
        if (!tribe.IsRunnableKind)
        {
            StatusLine = $"{tribe.Name}: {tribe.Kind} dailies aren't automated yet.";
            return false;
        }
        if (tribe.PrimaryIssuer is not { } issuer)
        {
            StatusLine = $"{tribe.Name}: no issuer in the sheet.";
            return false;
        }
        var standing = _state.Read(tribe);
        if (!standing.Unlocked)
        {
            StatusLine = $"{tribe.Name}: not unlocked (rank 0).";
            return false;
        }
        var slots = Math.Min(standing.SlotsLeft, _state.AllowanceLeft);
        _toRun = new Queue<ushort>(standing.AcceptedDailies); // finish any already in the journal too
        if (slots <= 0 && _toRun.Count == 0)
        {
            StatusLine = $"{tribe.Name}: nothing left today (slots {standing.SlotsLeft}, allowance {_state.AllowanceLeft}).";
            return false;
        }
        _tribe = tribe;
        _issuer = issuer;
        _acceptTarget = standing.TakenToday + slots;
        Enter(_world.IsCombatJob ? TribeRunState.Travel : TribeRunState.Job);
        _log($"{tribe.Name}: starting — accept up to {slots}, {_toRun.Count} already accepted.");
        return true;
    }

    public void Stop()
    {
        if (State == TribeRunState.Run && _controller.State != RunState.Idle)
            _controller.Stop();
        _tribe = null;
        State = TribeRunState.Idle;
    }

    public void Tick()
    {
        if (_tribe is null || State is TribeRunState.Idle or TribeRunState.Done or TribeRunState.Faulted)
            return;
        try
        {
            switch (State)
            {
                case TribeRunState.Job: TickJob(); break;
                case TribeRunState.Travel: TickTravel(); break;
                case TribeRunState.Accept: TickAccept(); break;
                case TribeRunState.Run: TickRun(); break;
            }
        }
        catch (Exception ex)
        {
            Fault($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void TickJob()
    {
        if (_world.IsCombatJob) { Enter(TribeRunState.Travel); return; }
        var sets = _world.CombatGearsets();
        if (sets.Count == 0) { Fault($"{_tribe!.Name}: no combat gearset to switch to."); return; }
        if (_world.UtcNow - _lastInteract > TimeSpan.FromSeconds(1))
        {
            _world.EquipGearset(sets[0]);
            _lastInteract = _world.UtcNow;
        }
        if (_world.UtcNow - _phaseStart > JobStall)
            Fault($"{_tribe!.Name}: could not switch to a combat job.");
    }

    private void TickTravel()
    {
        var issuer = _issuer!;
        if (_world.TerritoryId == issuer.TerritoryId
            && Vector3.Distance(_world.PlayerPosition, issuer.Position) <= StepExecutor.DefaultStopDistance + StepExecutor.ArrivalSlack)
        {
            _travelExecutor.Cancel();
            Enter(TribeRunState.Accept);
            return;
        }
        // Reuse the step executor for the walk/teleport to the issuer position.
        if (_travelExecutor.Status != StepStatus.Running)
        {
            var aetheryte = _issuerAetheryte();
            _travelExecutor.Begin(new QuestStep
            {
                Kind = StepKind.WalkTo, KindName = "WalkTo",
                Position = issuer.Position, TerritoryId = issuer.TerritoryId,
                AetheryteShortcut = aetheryte,
            });
        }
        StatusLine = $"{_tribe!.Name}: going to the issuer";
        if (_travelExecutor.Tick() == StepStatus.Failed)
            Fault($"{_tribe!.Name}: could not reach the issuer — {_travelExecutor.FailReason}");
    }

    /// <summary>Odysseus's own travel picks the nearest aetheryte; leave null and let the executor teleport by distance.</summary>
    private string? _issuerAetheryte() => null;

    private void TickAccept()
    {
        var standing = _state.Read(_tribe!);
        StatusLine = $"{_tribe!.Name}: accepting {standing.TakenToday}/{_acceptTarget}";

        if (standing.TakenToday >= _acceptTarget)
        {
            foreach (var id in standing.AcceptedDailies)
                if (!_toRun.Contains(id))
                    _toRun.Enqueue(id);
            _log($"{_tribe.Name}: accepted; {_toRun.Count} to run.");
            Enter(TribeRunState.Run);
            return;
        }

        // Answer whatever the issuer put up, else (re)interact.
        if (_world.IsAddonVisible("SelectYesno")) { _world.SelectYesNo(true); Bump(); return; }
        if (_world.IsAddonVisible("JournalAccept")) { _world.SelectYesNo(true); Bump(); return; } // confirm = the yes-callback
        if (_world.IsAddonVisible("SelectIconString")) { _world.SelectIconStringIndex(0); Bump(); return; }
        if (_world.IsAddonVisible("SelectString")) { _world.SelectStringIndex(0); Bump(); return; }

        if (_world.IsOccupied)
            return; // mid-conversation; wait

        if (_world.UtcNow - _lastInteract > ReinteractGap)
        {
            if (_issuer!.ENpcId != 0 && !_world.TryInteractWithDataId(_issuer.ENpcId))
                _log($"{_tribe.Name}: issuer {_issuer.ENpcId} not in reach.");
            _lastInteract = _world.UtcNow;
        }
        if (_world.UtcNow - _phaseStart > AcceptStall)
            Fault($"{_tribe.Name}: could not accept at the issuer.");
    }

    private void Bump()
    {
        _lastInteract = _world.UtcNow;
        _phaseStart = _world.UtcNow; // progress: reset the stall clock
    }

    private void TickRun()
    {
        if (_running != 0)
        {
            // The runner owns the frame while it is active, so it must drive the controller too.
            _controller.Tick();

            if (_controller.State == RunState.Faulted)
            {
                _log($"{_tribe!.Name}: daily {_running} faulted ({_controller.StatusLine}) — dropping it.");
                _controller.Stop();
                _running = 0;
                return;
            }
            if (_controller.State != RunState.Idle)
            {
                StatusLine = $"{_tribe!.Name}: {_controller.StatusLine}";
                return; // still running
            }
            _running = 0; // controller went idle: the daily completed (or was stopped)
        }

        // Next daily still not complete.
        while (_toRun.Count > 0)
        {
            var id = _toRun.Dequeue();
            if (_controller.QuestId == id) continue;
            if (!_controller.Start(id))
            {
                _log($"{_tribe!.Name}: no path/refused for daily {id} — skipping.");
                continue;
            }
            _running = id;
            StatusLine = $"{_tribe!.Name}: running daily {id}";
            return;
        }

        Enter(TribeRunState.Done);
        StatusLine = $"{_tribe!.Name}: done.";
        _log(StatusLine);
    }

    private void Enter(TribeRunState state)
    {
        State = state;
        _phaseStart = _world.UtcNow;
        _lastInteract = default;
    }

    private void Fault(string reason)
    {
        _travelExecutor.Cancel();
        if (_controller.State != RunState.Idle) _controller.Stop();
        State = TribeRunState.Faulted;
        StatusLine = reason;
        _log($"FAULT: {reason}");
    }
}
