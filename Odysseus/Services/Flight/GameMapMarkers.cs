using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Odysseus.Services.Flight;

/// <summary>
/// Puts <see cref="MapPin"/>s on the game's map and minimap.
///
/// <para>
/// The map agent rebuilds its marker list whenever the map is opened or changed, and the map
/// draws what that build left. Markers appended afterwards sit in the list unseen, so ours go in
/// from inside the build: a hook on <c>CreateMapMarkers</c> adds them after the game's own, on
/// the second of its two calls (the one that includes aetherytes). The pins are asked for by the
/// map's selected territory, so another zone's map gets that zone's.
/// </para>
///
/// <para>
/// The map addon has a fixed pool of marker nodes, smaller than the list: past about 100 markers
/// it reads a null node and the game crashes (as the aetherradar plugin found). Ours stop there.
/// </para>
///
/// <para>
/// The minimap is built the same way (<c>CreateMiniMapMarkers</c>, for the zone you stand in) into
/// a list of 100; ours stop at 60 in all, well inside it. Whether it is called once or twice per
/// build, ours go in once: they are skipped when they already sit at the end of the list.
/// </para>
/// </summary>
public sealed unsafe class GameMapMarkers : IDisposable
{
    private const int MarkerLimit = 100;
    private const int MiniMarkerLimit = 60;

    private readonly Hook<AgentMap.Delegates.CreateMapMarkers>? _hook;
    private readonly Hook<AgentMap.Delegates.CreateMiniMapMarkers>? _miniHook;
    // Where ours went in the minimap list last time, to tell a second call from a fresh build.
    private int _miniFrom = -1, _miniCount;
    private readonly Func<uint, IReadOnlyList<MapPin>> _pinsFor;
    private readonly Action<string> _log;
    private (uint Territory, int Count) _logged;

    // Label text the game keeps a pointer to for as long as the marker lives. Never freed: the map
    // may still be drawing a label, and the few bytes are not worth a crash.
    private readonly Dictionary<string, nint> _labels = new();

    public GameMapMarkers(IGameInteropProvider interop, Func<uint, IReadOnlyList<MapPin>> pinsFor, Action<string> log)
    {
        _pinsFor = pinsFor;
        _log = log;
        try
        {
            _hook = interop.HookFromAddress<AgentMap.Delegates.CreateMapMarkers>(
                (nint)AgentMap.MemberFunctionPointers.CreateMapMarkers, CreateMapMarkers);
            _hook.Enable();
            _miniHook = interop.HookFromAddress<AgentMap.Delegates.CreateMiniMapMarkers>(
                (nint)AgentMap.MemberFunctionPointers.CreateMiniMapMarkers, CreateMiniMapMarkers);
            _miniHook.Enable();
        }
        catch (Exception ex)
        {
            _log($"Aether currents on the map are unavailable ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private void CreateMapMarkers(AgentMap* agent, bool omitAetherytes)
    {
        _hook!.Original(agent, omitAetherytes);
        if (omitAetherytes) return;
        try
        {
            var territory = agent->SelectedTerritoryId;
            if (territory == 0) return;
            var pins = _pinsFor(territory);
            var added = 0;
            foreach (var pin in pins)
            {
                if (agent->MapMarkerCount >= MarkerLimit) break;
                if (agent->AddMapMarker(pin.At, pin.Icon, 0, Label(pin.Label))) added++;
            }
            if (_logged != (territory, added) && pins.Count > 0)
            {
                _logged = (territory, added);
                _log($"Map: {added} of {pins.Count} aether current marker(s) on the map of territory {territory}.");
            }
        }
        catch (Exception ex)
        {
            _log($"Map markers failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CreateMiniMapMarkers(AgentMap* agent, bool omitAetherytes)
    {
        _miniHook!.Original(agent, omitAetherytes);
        try
        {
            var territory = agent->CurrentTerritoryId;
            if (territory == 0) return;
            var pins = _pinsFor(territory);
            if (pins.Count == 0) return;

            var markers = agent->MiniMapMarkers;
            if (_miniFrom >= 0 && _miniCount > 0 && agent->MiniMapMarkerCount == _miniFrom + _miniCount
                && _miniCount <= pins.Count
                && markers[_miniFrom].MapMarker.IconId == pins[0].Icon
                && markers[_miniFrom + _miniCount - 1].MapMarker.IconId == pins[_miniCount - 1].Icon)
                return;   // ours are already in this build

            _miniFrom = agent->MiniMapMarkerCount;
            _miniCount = 0;
            foreach (var pin in pins)
            {
                if (agent->MiniMapMarkerCount >= MiniMarkerLimit) break;
                if (agent->AddMiniMapMarker(pin.At, pin.Icon)) _miniCount++;
            }
        }
        catch (Exception ex)
        {
            _log($"Minimap markers failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private byte* Label(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (!_labels.TryGetValue(text, out var at))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            at = Marshal.AllocHGlobal(bytes.Length + 1);
            Marshal.Copy(bytes, 0, at, bytes.Length);
            ((byte*)at)[bytes.Length] = 0;
            _labels[text] = at;
        }
        return (byte*)at;
    }

    /// <summary>The map flag on a spot, and the map opened on it.</summary>
    public static void Flag(uint territory, uint mapId, Vector3 at, string title)
    {
        var agent = AgentMap.Instance();
        if (agent is null) return;
        agent->SetFlagMapMarker(territory, mapId, at);
        agent->OpenMap(mapId, territory, title);
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _miniHook?.Dispose();
    }
}
