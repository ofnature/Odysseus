using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// Lifestream, wrapped. Aetheryte teleports and aethernet hops — most of the cross-zone movement
/// in the path data. Every call fails open: without Lifestream a travel step fails with a reason
/// rather than throwing inside the run loop.
///
/// <para>
/// Gate names are <c>Lifestream.{Method}</c> as registered by its EzIPC provider (method
/// identifiers verified in the installed 2.5.4.16 build: <c>Teleport</c>, <c>AethernetTeleport</c>,
/// <c>IsBusy</c>, <c>Abort</c>). Aethernet destinations are passed as Lifestream's own display
/// names — which is what the path data carries after the <c>[City] </c> prefix — so no
/// resolution happens on our side.
/// </para>
/// </summary>
public sealed class LifestreamIpc
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;

    private ICallGateSubscriber<uint, byte, bool>? _teleport;
    private ICallGateSubscriber<string, bool>? _aethernetTeleport;
    private ICallGateSubscriber<bool>? _isBusy;
    private ICallGateSubscriber<object>? _abort;
    private bool _warned;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    /// <summary>Teleport to an aetheryte by id. Lifestream handles the confirmation dialog and queueing.</summary>
    public bool Teleport(uint aetheryteId) => Try(() =>
        (_teleport ??= _pluginInterface.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport")).InvokeFunc(aetheryteId, 0));

    /// <summary>Walk to and use the nearest aethernet shard to reach <paramref name="destination"/> (Lifestream display name).</summary>
    public bool AethernetTeleport(string destination) => Try(() =>
        (_aethernetTeleport ??= _pluginInterface.GetIpcSubscriber<string, bool>("Lifestream.AethernetTeleport")).InvokeFunc(destination));

    /// <summary>Lifestream is mid-task (walking to a shard, waiting on a teleport).</summary>
    public bool IsBusy => Try(() =>
        (_isBusy ??= _pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy")).InvokeFunc());

    public void Abort() => Try(() =>
    {
        (_abort ??= _pluginInterface.GetIpcSubscriber<object>("Lifestream.Abort")).InvokeAction();
        return true;
    });

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
                _log?.Invoke($"Lifestream unavailable ({ex.GetType().Name}) — teleports are disabled until it loads.");
            }
            return false;
        }
    }
}
