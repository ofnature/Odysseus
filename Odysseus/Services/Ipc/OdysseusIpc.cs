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
    public const string CanGatherGate = "Odysseus.Gather.CanGather";
    public const string GatherStartGate = "Odysseus.Gather.Start";
    public const string GatherStopGate = "Odysseus.Gather.Stop";
    public const string GatherIsRunningGate = "Odysseus.Gather.IsRunning";
    public const string GatherStatusGate = "Odysseus.Gather.GetStatusJson";

    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<uint, bool>? _canGather;
    private readonly ICallGateProvider<string, bool>? _gatherStart;
    private readonly ICallGateProvider<object?>? _gatherStop;
    private readonly ICallGateProvider<bool>? _gatherIsRunning;
    private readonly ICallGateProvider<string>? _gatherStatus;

    /// <param name="isBusy">
    /// Reads the live run state. Must never throw — an exception here surfaces inside another
    /// plugin's poll.
    /// </param>
    /// <param name="gather">
    /// The gathering gateway, when there is one. Null publishes only <see cref="IsBusyGate"/>, which
    /// is what a build without the gatherer should look like from outside: a gate that is not there
    /// reads as "not available", never as "available and refusing".
    /// </param>
    public OdysseusIpc(IDalamudPluginInterface pluginInterface, Func<bool> isBusy, Gathering.GatherGateway? gather = null)
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

        if (gather is null)
            return;

        // The gathering surface. Same rules as above: never throw into a caller's poll, and a
        // refusal is always a false with a reason in the log rather than a silent partial run.
        _canGather = pluginInterface.GetIpcProvider<uint, bool>(CanGatherGate);
        _canGather.RegisterFunc(itemId => Guard(() => gather.CanGather(itemId), false));

        _gatherStart = pluginInterface.GetIpcProvider<string, bool>(GatherStartGate);
        _gatherStart.RegisterFunc(json => Guard(() => gather.Start(json), false));

        _gatherStop = pluginInterface.GetIpcProvider<object?>(GatherStopGate);
        _gatherStop.RegisterAction(() => Guard<object?>(() => { gather.Stop(); return null; }, null));

        // Told apart from Odysseus.IsBusy on purpose: a caller needs to know that the run still
        // going is *its* run, not a quest the player started while it was waiting.
        _gatherIsRunning = pluginInterface.GetIpcProvider<bool>(GatherIsRunningGate);
        _gatherIsRunning.RegisterFunc(() => Guard(() => gather.IsRunning, false));

        _gatherStatus = pluginInterface.GetIpcProvider<string>(GatherStatusGate);
        _gatherStatus.RegisterFunc(() => Guard(gather.StatusJson, "{}"));
    }

    public void Dispose()
    {
        _isBusy.UnregisterFunc();
        _canGather?.UnregisterFunc();
        _gatherStart?.UnregisterFunc();
        _gatherStop?.UnregisterAction();
        _gatherIsRunning?.UnregisterFunc();
        _gatherStatus?.UnregisterFunc();
    }

    /// <summary>Anything our side throws must stay our side; the caller gets the fallback.</summary>
    private static T Guard<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch
        {
            return fallback;
        }
    }
}
