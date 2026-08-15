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

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _logDegraded;

    private ICallGateSubscriber<ulong, object>? _recordExternalWrite;
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
}
