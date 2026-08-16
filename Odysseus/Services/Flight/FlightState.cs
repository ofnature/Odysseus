using System;

namespace Odysseus.Services.Flight;

/// <summary>Which aether currents this character has.</summary>
public interface IFlightState
{
    bool IsUnlocked(uint aetherCurrentId);
}

/// <summary>The real reader — the game keeps a bitfield of attuned currents.</summary>
public sealed unsafe class FlightState : IFlightState
{
    private readonly Action<string>? _log;

    public FlightState(Action<string>? log = null) => _log = log;

    public bool IsUnlocked(uint aetherCurrentId)
    {
        try
        {
            var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            return state != null && state->IsAetherCurrentUnlocked(aetherCurrentId);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Aether current read failed: {ex.Message}");
            return false;
        }
    }
}
