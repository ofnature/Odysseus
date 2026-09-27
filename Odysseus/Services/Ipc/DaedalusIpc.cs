using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// What Odysseus calls on Daedalus. Fails open — a missing or older Daedalus is a degraded
/// feature, never an exception on our side.
/// </summary>
public sealed class DaedalusIpc
{
    private const string RecordExternalWriteGate = "Daedalus.Targeting.RecordExternalWrite";
    private const string IsDisabledByUserGate = "Daedalus.IsDisabledByUser";
    private const string HoldActionsGate = "Daedalus.HoldActions";

    /// <summary>What Odysseus calls itself when it holds Daedalus's actions.</summary>
    public const string HoldOwner = "odysseus";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _logDegraded;

    private ICallGateSubscriber<ulong, object>? _recordExternalWrite;
    private ICallGateSubscriber<bool>? _isDisabledByUser;
    private ICallGateSubscriber<string, bool, bool>? _holdActions;
    private bool _warnedMissing;

    public DaedalusIpc(IDalamudPluginInterface pluginInterface, Action<string>? logDegraded = null)
    {
        _pluginInterface = pluginInterface;
        _logDegraded = logDegraded;
    }

    /// <summary>
    /// Tells Daedalus that the hard-target write we are about to make is automation, not the user.
    ///
    /// <para>
    /// Daedalus arms a four-second "hands off the wheel" grace whenever the hard target changes
    /// without one of its own writers claiming it, and holds its movement pulses for the duration.
    /// Without this call every target Odysseus sets looks like the user clicking a mob and Daedalus
    /// quietly stops moving for four seconds each time. Call immediately before the write.
    /// (Theseus finding #6.)
    /// </para>
    /// </summary>
    public void RecordTargetWrite(ulong gameObjectId)
    {
        if (gameObjectId == 0)
            return;

        try
        {
            _recordExternalWrite ??= _pluginInterface.GetIpcSubscriber<ulong, object>(RecordExternalWriteGate);
            _recordExternalWrite.InvokeAction(gameObjectId);
            _warnedMissing = false;
        }
        catch (Exception ex)
        {
            if (!_warnedMissing)
            {
                _warnedMissing = true;
                _logDegraded?.Invoke(
                    $"{RecordExternalWriteGate} unavailable ({ex.GetType().Name}) — Daedalus will " +
                    "read our retargets as manual clicks and hold its movement pulses.");
            }
        }
    }

    /// <summary>
    /// The user switched Daedalus OFF, so it will not fight for us. Not the same as "not enabled":
    /// Daedalus's switch starts off and still fights for automation until someone presses Disable.
    /// False when Daedalus is missing or too old to say — the pull then goes ahead as before.
    /// </summary>
    public bool IsDisabledByUser()
    {
        try
        {
            _isDisabledByUser ??= _pluginInterface.GetIpcSubscriber<bool>(IsDisabledByUserGate);
            return _isDisabledByUser.InvokeFunc();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hold every action Daedalus would submit, or let it go — for a quest item that has to go on a
    /// mob before Daedalus kills it. Auto-attack is the game's and keeps swinging, which is the
    /// controlled damage wanted. Daedalus treats the hold as a lease that lapses a few seconds after
    /// the last call, so a crashed Odysseus cannot leave the character idle. False when Daedalus is
    /// missing, too old to have the gate, or another plugin already holds it.
    /// </summary>
    public bool HoldActions(bool hold)
    {
        try
        {
            _holdActions ??= _pluginInterface.GetIpcSubscriber<string, bool, bool>(HoldActionsGate);
            return _holdActions.InvokeFunc(HoldOwner, hold);
        }
        catch
        {
            return false;
        }
    }
}
