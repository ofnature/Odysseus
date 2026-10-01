using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Odysseus.Services.Run;

/// <summary>
/// "/od record MateriaAttach" — writes to the log exactly what a window sends the game when you
/// click in it, so a click can be automated from what the game itself sends rather than from a
/// guess. The values dump says what a window shows; this says what pressing it does.
///
/// <para>
/// Every addon's click reaches the game through <c>AtkUnitBase.FireCallback</c>, so one hook there
/// covers every window. It is created only when a recording is first asked for, left disabled while
/// no window is being recorded, and passes every call through untouched.
/// </para>
/// </summary>
public sealed unsafe class CallbackRecorder : IDisposable
{
    private delegate byte FireCallbackDelegate(AtkUnitBase* unit, uint valueCount, AtkValue* values, byte close);

    private readonly IGameInteropProvider _interop;
    private readonly Action<string> _log;
    private readonly HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);
    private Hook<FireCallbackDelegate>? _hook;

    public CallbackRecorder(IGameInteropProvider interop, Action<string> log)
    {
        _interop = interop;
        _log = log;
    }

    /// <summary>Start recording a window, or stop if it already is. Returns what to tell the player.</summary>
    public string Toggle(string addonName)
    {
        try
        {
            if (!_watched.Remove(addonName))
                _watched.Add(addonName);

            _hook ??= _interop.HookFromAddress<FireCallbackDelegate>(
                (nint)AtkUnitBase.Addresses.FireCallback.Value, Detour);
            if (_watched.Count > 0) _hook.Enable();
            else _hook.Disable();

            return _watched.Contains(addonName)
                ? $"Recording what {addonName} sends — click in it, then \"/od record {addonName}\" again to stop. It goes to the log."
                : $"Stopped recording {addonName}.";
        }
        catch (Exception ex)
        {
            _watched.Remove(addonName);
            return $"Recording {addonName} failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private byte Detour(AtkUnitBase* unit, uint valueCount, AtkValue* values, byte close)
    {
        try
        {
            var name = unit == null ? string.Empty : unit->NameString;
            if (_watched.Contains(name))
            {
                var parts = new List<string>((int)valueCount);
                for (var i = 0; i < valueCount; i++)
                    parts.Add(GameStepWorld.DescribeValue(values[i]));
                _log($"{name} sent [{string.Join(" | ", parts)}]{(close != 0 ? " (closing)" : string.Empty)}");
            }
        }
        catch
        {
            // Recording must never get in the way of the click itself.
        }
        return _hook!.Original(unit, valueCount, values, close);
    }

    // ── What a window's agent receives ──
    //
    // A choice made in a context menu never passes through the window's own callback: the menu
    // hands it straight to the agent that owns the window. The Bestiary's "Assign to First
    // Battlehorn" is one — and the menu cannot be raised by a plugin (right-click is not an event
    // type the client exposes), so what the agent receives is the only way to repeat it.

    private delegate AtkValue* AgentReceiveEventDelegate(AgentInterface* agent, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind);

    private readonly Dictionary<nint, Hook<AgentReceiveEventDelegate>> _agentHooks = new();
    private readonly Dictionary<nint, (nint Agent, string Label)> _agentWatched = new();

    /// <summary>"/od record agentof XBMMonsterNotebook" — log everything the window's agent receives. Again to stop.</summary>
    public string ToggleAgentOf(string addonName, IGameGui gui)
    {
        try
        {
            var addon = gui.GetAddonByName(addonName);
            if (addon.IsNull)
                return $"{addonName} is not open — open it first.";
            var id = ((AtkUnitBase*)addon.Address)->Id;
            var module = AgentModule.Instance();
            if (module == null)
                return "The agent module is not there.";

            AgentInterface* owner = null;
            var index = -1;
            for (var i = 0; i < 509; i++)
            {
                var agent = module->GetAgentByInternalId((AgentId)i);
                if (agent != null && agent->AddonId == id)
                {
                    owner = agent;
                    index = i;
                    break;
                }
            }
            if (owner == null)
                return $"No agent owns {addonName} (addon id {id}).";

            var receive = *(nint*)*(nint*)owner;   // AtkEventInterface.ReceiveEvent, vtable slot 0
            if (_agentWatched.Remove(receive))
            {
                if (_agentHooks.Remove(receive, out var old)) old.Dispose();
                return $"Stopped recording {addonName}'s agent.";
            }

            var label = $"{addonName}'s agent (AgentId {index})";
            _agentWatched[receive] = ((nint)owner, label);
            Hook<AgentReceiveEventDelegate>? hook = null;
            hook = _interop.HookFromAddress<AgentReceiveEventDelegate>(receive, (agent, returnValue, values, count, kind) =>
            {
                try
                {
                    if (_agentWatched.TryGetValue(receive, out var watched) && (nint)agent == watched.Agent)
                    {
                        var parts = new List<string>((int)count);
                        for (var i = 0; i < count; i++)
                            parts.Add(GameStepWorld.DescribeValue(values[i]));
                        _log($"{watched.Label} received [{string.Join(" | ", parts)}] kind {kind}");
                    }
                }
                catch
                {
                    // never in the way of the event itself
                }
                return hook!.Original(agent, returnValue, values, count, kind);
            });
            hook.Enable();
            _agentHooks[receive] = hook;
            return $"Recording what {label} receives — do it by hand, then \"/od record agentof {addonName}\" again to stop.";
        }
        catch (Exception ex)
        {
            return $"Recording {addonName}'s agent failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        foreach (var hook in _agentHooks.Values) hook.Dispose();
        _agentHooks.Clear();
    }
}
