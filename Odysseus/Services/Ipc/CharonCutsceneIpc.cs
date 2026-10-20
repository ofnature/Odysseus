using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// Charon's cutscene lease, for the roleplay choice: during a run, cutscenes are skipped or watched
/// as the player chose, whatever the box's own Charon toggle says.
///
/// <para>
/// <c>Charon.Cutscene.Set(skip, seconds)</c> is a lease with a time limit (Charon caps it at 300):
/// skip=true skips, false WATCHES — it holds cutscenes even on a box whose toggle skips them. It is
/// renewed while a run drives the character and released (0) when the run ends, and a crash simply
/// stops the renewals, so a box is never left skipping or watching on our say-so. Charon not loaded
/// is not an error: TextAdvance and Odysseus's own prompt answer still follow the same choice.
/// </para>
/// </summary>
public sealed class CharonCutsceneIpc
{
    private const string SetGate = "Charon.Cutscene.Set";
    private static readonly TimeSpan RenewEvery = TimeSpan.FromSeconds(30);
    private const int LeaseSeconds = 90;

    private readonly IDalamudPluginInterface _pluginInterface;
    private ICallGateSubscriber<bool, int, bool>? _set;
    private DateTime _renewedAt;
    private bool? _leased;   // what Charon was last told, while a lease is live

    public CharonCutsceneIpc(IDalamudPluginInterface pluginInterface) => _pluginInterface = pluginInterface;

    /// <summary>Every frame: renew the lease while a run drives the character, release it after.</summary>
    public void Tick(bool running, bool skip, DateTime now)
    {
        if (!running)
        {
            if (_leased is { } held && Set(held, 0))
                _leased = null;
            return;
        }
        if (_leased == skip && now - _renewedAt < RenewEvery)
            return;
        if (now - _renewedAt < RenewEvery && _leased is null)
            return;   // Charon was not there a moment ago: ask on the next renewal, not every frame
        _renewedAt = now;
        if (Set(skip, LeaseSeconds))
            _leased = skip;
    }

    private bool Set(bool skip, int seconds)
    {
        try
        {
            return (_set ??= _pluginInterface.GetIpcSubscriber<bool, int, bool>(SetGate)).InvokeFunc(skip, seconds);
        }
        catch
        {
            return false;   // Charon not loaded, or too old to have the gate
        }
    }
}
