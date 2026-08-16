using System;

namespace Odysseus.Services.Deliveries;

/// <summary>
/// The delivery-specific parts of the game the runner needs, beyond what <c>IStepWorld</c> covers.
///
/// <para>
/// Turning a delivery in is two separate windows: the client's supply window picks a route, then
/// the ordinary NPC-trade window picks which of your matching items to hand over. Both are driven
/// through their agents rather than by clicking, which is why they are here and not in the step
/// executor — no quest step ever needs them.
/// </para>
/// </summary>
public interface IDeliveryWorld
{
    /// <summary>The supply window is open and belongs to this client.</summary>
    bool IsSupplyOpen(DeliveryClient client);

    /// <summary>Pick a route in the open supply window; the trade window follows.</summary>
    void OpenRoute(DeliveryRoute route);

    /// <summary>The trade window is up and asking for this item.</summary>
    bool IsTradeOpen(uint itemId);

    /// <summary>Hand over the first matching item and confirm. Returns false when the agent refused.</summary>
    bool CommitTrade(DeliveryRoute route);

    /// <summary>How many of an item are in the inventory, collectables included.</summary>
    int ItemCount(uint itemId);
}

/// <summary>The live implementation, driving <c>AgentSatisfactionSupply</c> and <c>AgentNpcTrade</c>.</summary>
public sealed unsafe class GameDeliveryWorld : IDeliveryWorld
{
    private readonly Action<string> _log;

    public GameDeliveryWorld(Action<string> log) => _log = log;

    public bool IsSupplyOpen(DeliveryClient client)
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentSatisfactionSupply.Instance();
            if (agent == null || !agent->IsAgentActive()) return false;
            var info = agent->NpcInfo;
            return info.Valid && info.Initialized && info.Id == client.Index;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Event 1 with the slot number is "show me this route's request".</summary>
    public void OpenRoute(DeliveryRoute route)
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentSatisfactionSupply.Instance();
            if (agent == null || !agent->IsAgentActive()) return;

            var result = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkValue);
            Span<FFXIVClientStructs.FFXIV.Component.GUI.AtkValue> args = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[2];
            args[0].SetInt(1);
            args[1].SetInt(SlotOf(route));
            fixed (FFXIVClientStructs.FFXIV.Component.GUI.AtkValue* p = args)
                agent->ReceiveEvent(&result, p, 2, 0);
        }
        catch (Exception ex)
        {
            _log($"Opening the {route} route failed: {ex.Message}");
        }
    }

    public bool IsTradeOpen(uint itemId)
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentNpcTrade.Instance();
            if (agent == null || !agent->IsAgentActive()) return false;
            var requests = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance()->NpcTrade.Requests;
            return requests.Count == 1 && requests.Items[0].ItemId == itemId;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Three events in order: start the turn-in for the slot, choose the first offered item, then
    /// commit. Each is checked before the next — a half-finished trade leaves the window stuck.
    /// </summary>
    public bool CommitTrade(DeliveryRoute route)
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentNpcTrade.Instance();
            if (agent == null || !agent->IsAgentActive())
            {
                _log("Turn-in refused: the trade agent is not active.");
                return false;
            }
            if (agent->SelectedTurnInSlot >= 0)
            {
                _log($"Turn-in refused: slot {agent->SelectedTurnInSlot} is already in progress.");
                return false;
            }

            var slot = SlotOf(route);
            var result = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkValue);
            Span<FFXIVClientStructs.FFXIV.Component.GUI.AtkValue> args = stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[4];
            args[0].SetInt(2);      // begin
            args[1].SetInt(slot);
            args[2].SetInt(0);
            args[3].SetInt(0);
            fixed (FFXIVClientStructs.FFXIV.Component.GUI.AtkValue* p = args)
            {
                agent->ReceiveEvent(&result, p, 4, 0);
                if (agent->SelectedTurnInSlot != slot || agent->SelectedTurnInSlotItemOptions <= 0)
                {
                    _log($"Turn-in did not start: slot={agent->SelectedTurnInSlot}, options={agent->SelectedTurnInSlotItemOptions}.");
                    return false;
                }

                args[0].SetInt(0);  // choose the first offered item
                args[1].SetInt(0);
                agent->ReceiveEvent(&result, p, 4, 1);
                if (agent->SelectedTurnInSlot >= 0)
                {
                    _log($"Turn-in was not confirmed: slot={agent->SelectedTurnInSlot}.");
                    return false;
                }

                var addonId = agent->AddonId;
                agent->ReceiveEvent(&result, p, 4, 0);   // commit
                var addon = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);
                if (addon != null && addon->IsVisible)
                    addon->Close(false);
            }
            return true;
        }
        catch (Exception ex)
        {
            _log($"Turn-in failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public int ItemCount(uint itemId)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            return manager == null ? 0 : manager->GetInventoryItemCount(itemId);
        }
        catch
        {
            return 0;
        }
    }

    private static int SlotOf(DeliveryRoute route) => (int)route - 1;
}
