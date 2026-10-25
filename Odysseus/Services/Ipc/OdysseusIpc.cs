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
    public const string QuestCanRunGate = "Odysseus.Quest.CanRun";
    public const string QuestStartGate = "Odysseus.Quest.Start";
    public const string QuestStopGate = "Odysseus.Quest.Stop";
    public const string QuestIsRunningGate = "Odysseus.Quest.IsRunning";
    public const string QuestStatusGate = "Odysseus.Quest.GetStatusJson";

    private readonly ICallGateProvider<bool> _isBusy;
    private readonly ICallGateProvider<uint, bool>? _questCanRun;
    private readonly ICallGateProvider<uint, bool>? _questStart;
    private readonly ICallGateProvider<object?>? _questStop;
    private readonly ICallGateProvider<bool>? _questIsRunning;
    private readonly ICallGateProvider<string>? _questStatus;

    /// <param name="isBusy">
    /// Reads the live run state. Must never throw — an exception here surfaces inside another
    /// plugin's poll.
    /// </param>
    /// <param name="quests">
    /// The quest gateway — another plugin asking for one quest to be done (Calypso, for the Island
    /// Sanctuary quests). Takes the quest's row id (70179) or Odysseus's own (4643). Null publishes none.
    /// </param>
    public OdysseusIpc(IDalamudPluginInterface pluginInterface, Func<bool> isBusy, Quest.QuestGateway? quests = null)
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

        if (quests is not null)
        {
            _questCanRun = pluginInterface.GetIpcProvider<uint, bool>(QuestCanRunGate);
            _questCanRun.RegisterFunc(id => Guard(() => quests.CanRun(id), false));

            _questStart = pluginInterface.GetIpcProvider<uint, bool>(QuestStartGate);
            _questStart.RegisterFunc(id => Guard(() => quests.Start(id), false));

            _questStop = pluginInterface.GetIpcProvider<object?>(QuestStopGate);
            _questStop.RegisterAction(() => Guard<object?>(() => { quests.Stop(); return null; }, null));

            // The caller's run only: a quest the player started meanwhile is not it.
            _questIsRunning = pluginInterface.GetIpcProvider<bool>(QuestIsRunningGate);
            _questIsRunning.RegisterFunc(() => Guard(() => quests.IsRunning, false));

            _questStatus = pluginInterface.GetIpcProvider<string>(QuestStatusGate);
            _questStatus.RegisterFunc(() => Guard(quests.StatusJson, "{}"));
        }
    }

    public void Dispose()
    {
        _isBusy.UnregisterFunc();
        _questCanRun?.UnregisterFunc();
        _questStart?.UnregisterFunc();
        _questStop?.UnregisterAction();
        _questIsRunning?.UnregisterFunc();
        _questStatus?.UnregisterFunc();
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
