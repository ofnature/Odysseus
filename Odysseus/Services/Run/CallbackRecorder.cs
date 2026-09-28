using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
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

    public void Dispose() => _hook?.Dispose();
}
