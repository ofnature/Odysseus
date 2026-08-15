using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// What Odysseus calls on Theseus: hand it a dungeon or trial that sits inside an MSQ quest.
///
/// <para>
/// <c>Theseus.EnterDuty(cfc)</c> begins entry through Duty Support and runs the duty on arrival;
/// <c>Theseus.IsBusy</c> stays true until the run is over. Odysseus does nothing inside — it waits
/// for busy to fall and the character to be back outside, then reads its own quest state to see
/// the sequence moved. Every call fails open; a missing Theseus is a step that stops and says so.
/// </para>
/// </summary>
public sealed class TheseusIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<bool>? _isBusy;
    private ICallGateSubscriber<bool>? _canEnterDuty;
    private ICallGateSubscriber<uint, bool>? _enterDuty;
    private bool _warned;

    public TheseusIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    public bool IsBusy => Try(() =>
        (_isBusy ??= _pluginInterface.GetIpcSubscriber<bool>("Theseus.IsBusy")).InvokeFunc());

    public bool CanEnterDuty => Try(() =>
        (_canEnterDuty ??= _pluginInterface.GetIpcSubscriber<bool>("Theseus.CanEnterDuty")).InvokeFunc());

    public bool EnterDuty(uint contentFinderConditionId) => Try(() =>
        (_enterDuty ??= _pluginInterface.GetIpcSubscriber<uint, bool>("Theseus.EnterDuty")).InvokeFunc(contentFinderConditionId));

    private bool Try(Func<bool> call)
    {
        try
        {
            var result = call();
            _warned = false;
            return result;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Theseus unavailable ({ex.GetType().Name}) — duties inside quests will stop and wait for you.");
            }
            return false;
        }
    }
}
