using System;

namespace Odysseus.Services.Run;

public enum RepairState { Idle, Opening, Pressing, Confirming, Verifying, Closing, Done, Faulted }

/// <summary>
/// Self-repair: open the game's repair window, press Repair All, answer its confirmation, and
/// see the condition actually rise before calling it done. Gathering wears gear a point or two
/// a node, and a long list run walks into the durability wall otherwise. Dark matter and the
/// crafter levels are the character's business; when they are missing the window says so by
/// refusing, and this reports that rather than pressing on.
/// </summary>
public sealed class GearRepair
{
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ConfirmWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RepairWait = TimeSpan.FromSeconds(8);

    private readonly IStepWorld _world;
    private readonly Action<string> _log;
    private DateTime _phaseStart;
    private int _before;

    public GearRepair(IStepWorld world, Action<string> log)
    {
        _world = world;
        _log = log;
    }

    public RepairState State { get; private set; } = RepairState.Idle;
    public string FailReason { get; private set; } = string.Empty;
    public bool Busy => State is RepairState.Opening or RepairState.Pressing or RepairState.Confirming
        or RepairState.Verifying or RepairState.Closing;

    /// <summary>The worst-worn piece is at or under the threshold; a threshold of zero means never.</summary>
    public bool Needed(int thresholdPercent)
        => thresholdPercent > 0 && _world.LowestGearConditionPercent <= thresholdPercent;

    public void Begin()
    {
        _before = _world.LowestGearConditionPercent;
        FailReason = string.Empty;
        _log($"Gear is at {_before}% — repairing.");
        Enter(RepairState.Opening);
        _world.OpenRepairWindow();
    }

    public void Cancel()
    {
        if (Busy)
            _world.CloseRepairWindow();
        State = RepairState.Idle;
    }

    public void Tick()
    {
        var now = _world.UtcNow;
        switch (State)
        {
            case RepairState.Opening:
                if (_world.IsAddonVisible("Repair"))
                    Enter(RepairState.Pressing);
                else if (now - _phaseStart > WindowWait)
                    Fault("the repair window did not open");
                break;

            case RepairState.Pressing:
                if (_world.PressRepairAll())
                    Enter(RepairState.Confirming);
                else if (now - _phaseStart > WindowWait)
                    Fault("Repair All could not be pressed — no dark matter, or nothing this character can repair");
                break;

            case RepairState.Confirming:
                // The game asks before spending the dark matter; a repair that needed no asking
                // shows up as the condition already rising.
                if (_world.IsAddonVisible("SelectYesno"))
                {
                    _world.SelectYesNo(true);
                    Enter(RepairState.Verifying);
                }
                else if (_world.LowestGearConditionPercent > _before || now - _phaseStart > ConfirmWait)
                    Enter(RepairState.Verifying);
                break;

            case RepairState.Verifying:
                if (_world.LowestGearConditionPercent > _before)
                    Enter(RepairState.Closing);
                else if (now - _phaseStart > RepairWait)
                    Fault("the gear did not repair — out of dark matter, or the crafter level is too low for it");
                break;

            case RepairState.Closing:
                _world.CloseRepairWindow();
                _log($"Gear repaired to {_world.LowestGearConditionPercent}%.");
                State = RepairState.Done;
                break;
        }
    }

    private void Enter(RepairState state)
    {
        State = state;
        _phaseStart = _world.UtcNow;
    }

    private void Fault(string reason)
    {
        _world.CloseRepairWindow();
        FailReason = reason;
        State = RepairState.Faulted;
        _log($"Repair gave up: {reason}.");
    }
}
