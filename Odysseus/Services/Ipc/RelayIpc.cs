using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace Odysseus.Services.Ipc;

/// <summary>
/// The Daedalus LAN relay, wrapped: <c>Daedalus.Relay.Publish(channel, json)</c> out and the
/// <c>Daedalus.Relay.Message</c> event in. Reaches every other client on the LAN and every sibling
/// client on this machine (Daedalus's loopback mirror), and never echoes our own frame back.
///
/// <para>
/// Odysseus does not open a socket of its own — same decision as Theseus. Without Daedalus the
/// publish is a no-op and no messages arrive; the dashboard then shows only this box.
/// </para>
/// </summary>
public sealed class RelayIpc : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Action<string>? _log;
    private ICallGateSubscriber<string, string, object?>? _publish;
    private ICallGateSubscriber<string, string, object?>? _message;
    private Action<string, string>? _handler;
    private bool _warned;

    public RelayIpc(IDalamudPluginInterface pluginInterface, Action<string>? log = null)
    {
        _pluginInterface = pluginInterface;
        _log = log;
    }

    public bool Publish(string channel, string json)
    {
        try
        {
            (_publish ??= _pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Publish"))
                .InvokeAction(channel, json);
            _warned = false;
            return true;
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log?.Invoke($"Daedalus relay unavailable ({ex.GetType().Name}) — fleet status stays local.");
            }
            return false;
        }
    }

    /// <summary>Receive every relay message; the handler filters by channel. Safe to call once.</summary>
    public void Subscribe(Action<string, string> handler)
    {
        try
        {
            _handler = handler;
            _message ??= _pluginInterface.GetIpcSubscriber<string, string, object?>("Daedalus.Relay.Message");
            _message.Subscribe(_handler);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Daedalus relay subscribe failed ({ex.GetType().Name}).");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_handler is not null)
                _message?.Unsubscribe(_handler);
        }
        catch
        {
            // Daedalus is gone; nothing to unsubscribe from.
        }
    }
}
