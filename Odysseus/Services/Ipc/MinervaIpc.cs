using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// Minerva, wrapped — the other duty AI a solo instance can be handed to.
///
/// <para>
/// Minerva has no on/off mode the way BossMod Reborn does. What it has is one <i>owned slot</i>: a
/// plugin claims it, Minerva's dodge settings are whatever that plugin asked for while it holds it,
/// and handing it back returns the user's own Default. Only the holder may hand it back, so two
/// drivers cannot fight over it several times a second. Odysseus holds the slot under the name
/// <see cref="Owner"/> for exactly as long as a fight lasts.
/// </para>
///
/// <para>
/// Two ways to claim it. <c>ApplyPreset</c> takes a whole named preset — clearance, positional arc,
/// navmesh use — for when the user has an opinion about how Minerva should fight. <c>SetAutoDodge</c>
/// just turns dodging on, for when they do not. Both hand back the same way.
/// </para>
///
/// <para>
/// Fail-open, like every other handoff: without Minerva the call does nothing and the fight is the
/// player's, rather than throwing into a step.
/// </para>
/// </summary>
public sealed class MinervaIpc
{
    /// <summary>What Odysseus calls itself when it claims Minerva's slot.</summary>
    public const string Owner = "odysseus";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<string, bool, bool>? _setAutoDodge;
    private ICallGateSubscriber<bool>? _isAutoDodgeEnabled;
    private ICallGateSubscriber<string, string, bool>? _applyPreset;
    private ICallGateSubscriber<string, bool>? _releasePreset;
    private ICallGateSubscriber<uint, bool>? _coversQuestBattle;
    private bool _warned;
    private bool _holding;

    public MinervaIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>
    /// Whether Minerva has a module for this quest's solo duty. False when it has none, or when
    /// Minerva is not loaded or too old to say — the safe answer for "can the fight be left to it".
    /// </summary>
    public bool CoversQuestBattle(ushort questId)
    {
        try
        {
            return (_coversQuestBattle ??= _pluginInterface.GetIpcSubscriber<uint, bool>("Minerva.CoversQuestBattle"))
                .InvokeFunc(questId);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Whether Odysseus believes it currently holds Minerva's slot.</summary>
    public bool Holding => _holding;

    /// <summary>
    /// Take the fight, or give it back.
    /// </summary>
    /// <param name="on">True to claim the slot for the fight, false to hand it back.</param>
    /// <param name="preset">
    /// The preset to apply, or empty to just switch dodging on. A name Minerva does not know is
    /// refused by Minerva rather than guessed at, and that refusal is reported here — a preset that
    /// silently did nothing would look exactly like a duty AI that did not work.
    /// </param>
    public bool Drive(bool on, string? preset = null)
    {
        try
        {
            if (!on)
            {
                _releasePreset ??= _pluginInterface.GetIpcSubscriber<string, bool>("Minerva.ReleasePreset");
                var released = _releasePreset.InvokeFunc(Owner);
                _holding = false;
                return released;
            }

            bool took;
            if (!string.IsNullOrWhiteSpace(preset))
            {
                // Minerva's gate is (name, owner) — not (owner, name).
                _applyPreset ??= _pluginInterface.GetIpcSubscriber<string, string, bool>("Minerva.ApplyPreset");
                took = _applyPreset.InvokeFunc(preset, Owner);
                if (!took)
                {
                    _log?.Invoke($"Minerva does not know a preset called \"{preset}\" — create it in Minerva, "
                        + "or clear the setting to just switch its dodging on.");
                    return false;
                }
            }
            else
            {
                _setAutoDodge ??= _pluginInterface.GetIpcSubscriber<string, bool, bool>("Minerva.SetAutoDodge");
                took = _setAutoDodge.InvokeFunc(Owner, true);
                if (!took)
                {
                    _log?.Invoke("Minerva's slot is held by another plugin — it will fight to whatever that "
                        + "plugin asked for, not to ours.");
                    return false;
                }
            }

            _holding = true;
            _warned = false;

            // Verified by re-reading, not by the return value: a preset that exists but has dodging
            // turned off is a preset that watches the fight rather than one that fights it.
            _isAutoDodgeEnabled ??= _pluginInterface.GetIpcSubscriber<bool>("Minerva.IsAutoDodgeEnabled");
            if (!_isAutoDodgeEnabled.InvokeFunc())
                _log?.Invoke(string.IsNullOrWhiteSpace(preset)
                    ? "Minerva took the fight but reports dodging off — check its settings."
                    : $"Minerva applied \"{preset}\" but that preset has dodging off, so it will watch rather than fight.");
            return true;
        }
        catch (Exception ex)
        {
            _holding = false;
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Minerva unavailable ({ex.GetType().Name}) — solo duties will stop and wait for you.");
            }
            return false;
        }
    }
}
