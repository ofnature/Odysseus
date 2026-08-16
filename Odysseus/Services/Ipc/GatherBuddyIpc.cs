using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// GatherBuddy Reborn, wrapped — the gathering handoff for custom deliveries.
///
/// <para>
/// <b>It is a switch, not a request.</b> Read out of GatherBuddyReborn 7.5.5.0, the whole exposed
/// surface is <c>Identify</c>, <c>Version</c>, <c>IsAutoGatherEnabled</c>,
/// <c>SetAutoGatherEnabled</c>, <c>IsAutoGatherWaiting</c>, <c>GetAutoGatherStatusText</c> and two
/// change notifications. There is nothing resembling Questionable's
/// <c>StartGatheringComplex(npc, item, job, quantity, collectability)</c>, so what to gather cannot
/// be passed across — it comes from GatherBuddy's own auto-gather lists.
/// </para>
///
/// <para>
/// So the handoff is: switch it on, watch our own bag until the count is reached, switch it off.
/// Progress is never asked for, only the stuck signal — <c>IsAutoGatherWaiting</c> with its status
/// text, which is how a timed node or a wrong job gets reported instead of silently spinning.
/// </para>
///
/// <para>
/// The item still has to be on one of your lists. The set a client can ever ask for is small and
/// fixed, so the Deliveries window lists it for you to add once — see
/// <c>DeliveryRequests.Possible</c>.
/// </para>
/// </summary>
public sealed class GatherBuddyIpc : Deliveries.IGatherer
{
    /// <summary>The version this wrapper was read from; anything older is not trusted.</summary>
    public const int KnownVersion = 1;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<int>? _version;
    private ICallGateSubscriber<bool>? _isEnabled;
    private ICallGateSubscriber<bool>? _isWaiting;
    private ICallGateSubscriber<string>? _statusText;
    private ICallGateSubscriber<bool, object>? _setEnabled;
    private bool _warned;

    public GatherBuddyIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>GatherBuddy is loaded and answering.</summary>
    public bool Available
    {
        get
        {
            try
            {
                _version ??= _pluginInterface.GetIpcSubscriber<int>("GatherBuddyReborn.Version");
                _version.InvokeFunc();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Its IPC version, or 0 when it is not there.</summary>
    public int Version
    {
        get
        {
            try
            {
                _version ??= _pluginInterface.GetIpcSubscriber<int>("GatherBuddyReborn.Version");
                return _version.InvokeFunc();
            }
            catch
            {
                return 0;
            }
        }
    }

    public bool IsRunning
    {
        get
        {
            try
            {
                _isEnabled ??= _pluginInterface.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherEnabled");
                return _isEnabled.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Running but not gathering — nothing it can reach right now.</summary>
    public bool IsWaiting
    {
        get
        {
            try
            {
                _isWaiting ??= _pluginInterface.GetIpcSubscriber<bool>("GatherBuddyReborn.IsAutoGatherWaiting");
                return _isWaiting.InvokeFunc();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Whatever it is telling its own window, which is the only reason it gives.</summary>
    public string Status
    {
        get
        {
            try
            {
                _statusText ??= _pluginInterface.GetIpcSubscriber<string>("GatherBuddyReborn.GetAutoGatherStatusText");
                return _statusText.InvokeFunc() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public bool Start() => Set(true);

    public void Stop() => Set(false);

    private bool Set(bool enabled)
    {
        try
        {
            (_setEnabled ??= _pluginInterface.GetIpcSubscriber<bool, object>("GatherBuddyReborn.SetAutoGatherEnabled"))
                .InvokeAction(enabled);
            _warned = false;
            return true;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"GatherBuddy unavailable ({ex.GetType().Name}) — gathering deliveries will stop and wait for you.");
            }
            return false;
        }
    }
}
