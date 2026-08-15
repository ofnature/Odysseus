using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// What Odysseus publishes for other plugins.
///
/// <para>
/// <b><c>Odysseus.IsBusy</c></b> — true while a run is driving the character. Daedalus polls this
/// through its <c>AutomationBusyBridge</c> and holds its external-combat override for as long as
/// it reads true, which is how quest mobs get killed: Odysseus owns movement and the quest,
/// Daedalus owns the rotation, and this one boolean is the entire handshake. Same shape as the
/// bridge's existing sources (<c>Theseus.IsBusy</c>, <c>Henchman.IsBusy</c>), so nothing new is
/// involved on either side — Daedalus just needs the one bridge entry added.
/// </para>
///
/// <para>
/// The gate is level-triggered and read about once a second, so it must reflect the truth right
/// now rather than latching. If Odysseus crashes or unloads mid-run the gate simply stops
/// existing, and Daedalus reads a missing gate as idle — the failure mode is "rotation stops",
/// never "rotation runs forever".
/// </para>
/// </summary>
public sealed class OdysseusIpc : IDisposable
{
    public const string IsBusyGate = "Odysseus.IsBusy";

    private readonly ICallGateProvider<bool> _isBusy;

    /// <param name="isBusy">
    /// Reads the live run state. Must never throw — an exception here surfaces inside another
    /// plugin's poll.
    /// </param>
    public OdysseusIpc(IDalamudPluginInterface pluginInterface, Func<bool> isBusy)
    {
        _isBusy = pluginInterface.GetIpcProvider<bool>(IsBusyGate);
        _isBusy.RegisterFunc(() =>
        {
            try
            {
                return isBusy();
            }
            catch
            {
                // Fail open to idle, matching how the consumer treats an unavailable gate.
                return false;
            }
        });
    }

    public void Dispose() => _isBusy.UnregisterFunc();
}
