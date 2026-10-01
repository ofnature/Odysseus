using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Common.Lua;

namespace Odysseus.Services.Run;

/// <summary>
/// Sniping sections (Securing the Saltery and 31 others, Stormblood onward) completed without the
/// minigame. The event asks the client to start the snipe task; this answers "hit" at once instead.
///
/// <para>
/// Ported from CBT's "Sniper no sniping" (<c>AutoSnipeQuests</c>, Jaksuhn/ffxiv-bundleoftweaks,
/// BSD-3-Clause, © 2023 Puni.sh — see NOTICE.md), whose signature credits xan. Odysseus switches it on
/// for a sniping step only, and off after.
/// </para>
/// </summary>
public sealed unsafe class SnipeSkipper : IDisposable
{
    private const string EnqueueSnipeTaskSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 48 83 EC 50 48 8B F9 48 8D 4C 24 ??";

    private delegate ulong EnqueueSnipeTaskDelegate(EventSceneModuleImplBase* scene, lua_State* state);

    private readonly Hook<EnqueueSnipeTaskDelegate>? _hook;

    public SnipeSkipper(IGameInteropProvider interop, Action<string> log)
    {
        try
        {
            _hook = interop.HookFromSignature<EnqueueSnipeTaskDelegate>(EnqueueSnipeTaskSignature, EnqueueSnipeTask);
        }
        catch (Exception ex)
        {
            log($"Sniping sections cannot be skipped this patch — the snipe task was not found ({ex.Message}).");
        }
    }

    /// <summary>Null when the game function was not found (a patch moved it): the player snipes.</summary>
    public bool? Enabled => _hook?.IsEnabled;

    public void Set(bool on)
    {
        if (_hook is null)
            return;
        if (on) _hook.Enable();
        else _hook.Disable();
    }

    private ulong EnqueueSnipeTask(EventSceneModuleImplBase* scene, lua_State* state)
    {
        try
        {
            // Hand the script its result — a number, 1 — rather than queueing the minigame.
            var value = state->top;
            value->tt = 3;
            value->value.n = 1;
            state->top += 1;
            return 1;
        }
        catch
        {
            return _hook!.Original(scene, state);
        }
    }

    public void Dispose() => _hook?.Dispose();
}
