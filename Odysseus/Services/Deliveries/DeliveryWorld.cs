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

    /// <summary>How many of an item are in the bags, collectables included.</summary>
    int ItemCount(uint itemId);

    /// <summary>The crafting job the player is on right now, as a <c>CraftType</c> index, or -1.</summary>
    int CurrentCraftType { get; }

    /// <summary>This shop's window is open.</summary>
    bool IsShopOpen(uint shopId);

    /// <summary>Interact with a vendor and pick its shop. False when the NPC or the shop is not there.</summary>
    bool OpenShop(uint vendorDataId, uint shopId);

    /// <summary>Buy from an open shop. False when the item is not on its shelves.</summary>
    bool BuyFromShop(uint shopId, uint itemId, int count);

    /// <summary>A purchase is still going through.</summary>
    bool ShopBusy(uint shopId);

    void CloseShop();

    /// <summary>Gil on hand.</summary>
    int Gil { get; }
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

    /// <summary>The four bag pages. Collectables sit here like anything else.</summary>
    private static readonly FFXIVClientStructs.FFXIV.Client.Game.InventoryType[] Bags =
    [
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory1,
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory2,
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory3,
        FFXIVClientStructs.FFXIV.Client.Game.InventoryType.Inventory4,
    ];

    /// <summary>
    /// Counted by walking the bags rather than through <c>GetInventoryItemCount</c>.
    ///
    /// <para>
    /// A delivery item is a collectable, and the convenience counter treats collectability as a
    /// filter — a freshly crafted Coerthan Souvenir sitting in the bag counted as zero, so the
    /// runner asked Artisan to make another. Matching on item id alone has no such trapdoor.
    /// </para>
    /// </summary>
    public int ItemCount(uint itemId)
    {
        try
        {
            var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
            if (manager == null) return 0;

            var total = 0;
            foreach (var bag in Bags)
            {
                var container = manager->GetInventoryContainer(bag);
                if (container == null || !container->IsLoaded) continue;
                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item != null && item->ItemId == itemId)
                        total += (int)item->Quantity;
                }
            }
            return total;
        }
        catch (Exception ex)
        {
            _log($"Counting item {itemId} failed: {ex.Message}");
            return 0;
        }
    }

    /// <summary>ClassJob 8..15 are CRP..CUL, in the same order as <c>Recipe.CraftType</c>.</summary>
    public int CurrentCraftType
    {
        get
        {
            try
            {
                var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
                if (state == null) return -1;
                var job = state->CurrentClassJobId;
                return job is >= 8 and <= 15 ? job - 8 : -1;
            }
            catch
            {
                return -1;
            }
        }
    }

    private static int SlotOf(DeliveryRoute route) => (int)route - 1;

    // ── Vendors ──
    //
    // A shop is an event handler, not an addon: opening one means interacting with the NPC and, if
    // it offers more than one thing, choosing the shop out of the event selector. Buying then goes
    // through the handler directly, which is how quantity is set without touching the numeric
    // stepper in the window.

    public bool IsShopOpen(uint shopId)
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
            if (agent == null || !agent->IsAgentActive() || agent->EventReceiver == null || !agent->IsAddonReady())
                return false;
            if (shopId == 0) return true;
            if (!Handler(shopId, out var handler)) return false;
            var proxy = (FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler.AgentProxy*)agent->EventReceiver;
            return proxy->Handler == handler;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenShop(uint vendorDataId, uint shopId)
    {
        try
        {
            var vendor = FindObject(vendorDataId);
            if (vendor == null)
            {
                _log($"Vendor {vendorDataId} is not nearby.");
                return false;
            }

            FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(vendor);

            // An NPC with one purpose opens straight away; one with several puts up a selector.
            var selector = FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandlerSelector.Instance();
            if (selector->Target == null) return true;
            if (selector->Target != vendor)
            {
                _log("The event selector is pointed at a different object.");
                return false;
            }

            for (var i = 0; i < selector->OptionsCount; i++)
            {
                if (selector->Options[i].Handler->Info.EventId.Id != shopId) continue;
                FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance()->InteractWithHandlerFromSelector(i);
                return true;
            }
            _log($"Shop {shopId:X} is not among what that NPC offers.");
            return false;
        }
        catch (Exception ex)
        {
            _log($"Opening shop {shopId:X} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool BuyFromShop(uint shopId, uint itemId, int count)
    {
        try
        {
            if (!Handler(shopId, out var handler)) return false;
            if (handler->Info.EventId.ContentId != FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandlerContent.Shop)
            {
                _log($"{shopId:X} is not a shop.");
                return false;
            }

            var shop = (FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler*)handler;
            for (var i = 0; i < shop->VisibleItemsCount; i++)
            {
                var index = shop->VisibleItems[i];
                if (shop->Items[index].ItemId != itemId) continue;
                shop->BuyItemIndex = index;
                shop->ExecuteBuy(count);
                return true;
            }
            _log($"Shop {shopId:X} does not stock item {itemId}.");
            return false;
        }
        catch (Exception ex)
        {
            _log($"Buying {count} × {itemId} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool ShopBusy(uint shopId)
    {
        try
        {
            if (!Handler(shopId, out var handler)) return false;
            var shop = (FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler*)handler;
            return shop->WaitingForTransactionToFinish;
        }
        catch
        {
            return false;
        }
    }

    public void CloseShop()
    {
        try
        {
            var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
            if (agent == null || agent->EventReceiver == null) return;
            var proxy = (FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler.AgentProxy*)agent->EventReceiver;
            proxy->Handler->CancelInteraction();
            var result = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkValue);
            var arg = default(FFXIVClientStructs.FFXIV.Component.GUI.AtkValue);
            arg.SetInt(-1);
            agent->ReceiveEvent(&result, &arg, 1, 0);
        }
        catch (Exception ex)
        {
            _log($"Closing the shop failed: {ex.Message}");
        }
    }

    /// <summary>Gil lives in the currency container, not the bags, so the plain counter is right here.</summary>
    public int Gil
    {
        get
        {
            try
            {
                var manager = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
                return manager == null ? 0 : manager->GetInventoryItemCount(1);
            }
            catch
            {
                return 0;
            }
        }
    }

    private static bool Handler(uint shopId, out FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandler* handler)
    {
        handler = null;
        var map = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance()->EventHandlerModule.EventHandlerMap;
        if (!map.TryGetValuePointer(shopId, out var entry) || entry == null || entry->Value == null)
            return false;
        handler = entry->Value;
        return true;
    }

    private static FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject* FindObject(uint dataId)
    {
        var objects = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
        if (objects == null) return null;
        foreach (var obj in objects->Objects.IndexSorted)
            if (obj.Value != null && obj.Value->BaseId == dataId)
                return obj.Value;
        return null;
    }
}
