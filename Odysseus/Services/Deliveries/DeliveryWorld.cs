using System;
using System.Collections.Generic;

namespace Odysseus.Services.Deliveries;

/// <summary>
/// Buying from an NPC. Split out of <see cref="IDeliveryWorld"/> because a <c>PurchaseItem</c>
/// quest step wants exactly this and nothing else about deliveries; the step executor takes the
/// narrow interface and <see cref="GameDeliveryWorld"/> keeps being the one implementation, so
/// there is no second copy of the shop-handler code to keep in step.
/// </summary>
public interface IShopWorld
{
    /// <summary>This shop's window is open. <c>0</c> asks only whether <i>a</i> shop is open.</summary>
    bool IsShopOpen(uint shopId);

    /// <summary>The event id of the shop that is open right now, or 0 when none is.</summary>
    uint OpenShopId { get; }

    /// <summary>
    /// Interact with a vendor and pick its shop. <paramref name="shopId"/> may be 0 for an NPC
    /// whose only purpose is the shop — then there is nothing to choose out of. False when the NPC
    /// or the shop is not there.
    /// </summary>
    bool OpenShop(uint vendorDataId, uint shopId);

    /// <summary>Buy from an open shop. False when the item is not on its shelves.</summary>
    bool BuyFromShop(uint shopId, uint itemId, int count);

    /// <summary>A purchase is still going through.</summary>
    bool ShopBusy(uint shopId);

    void CloseShop();

    /// <summary>Gil on hand.</summary>
    int Gil { get; }
}

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
public interface IDeliveryWorld : IShopWorld
{
    /// <summary>The supply window is open and belongs to this client.</summary>
    bool IsSupplyOpen(DeliveryClient client);

    /// <summary>Pick a route in the open supply window; the trade window follows.</summary>
    void OpenRoute(DeliveryRoute route);

    /// <summary>The trade window is up and asking for this item.</summary>
    bool IsTradeOpen(uint itemId);

    /// <summary>Hand over the first matching item and confirm. Returns false when the agent refused.</summary>
    bool CommitTrade(DeliveryRoute route);

    /// <summary>
    /// How many of an item are in the bags. <paramref name="minCollectability"/> above zero counts
    /// only collectables rated at least that high — a delivery will not take anything less, so a
    /// plain count would report items that cannot actually be handed over.
    /// </summary>
    int ItemCount(uint itemId, int minCollectability = 0);

    /// <summary>The crafting job the player is on right now, as a <c>CraftType</c> index, or -1.</summary>
    int CurrentCraftType { get; }

    /// <summary>A nearby NPC that runs this special shop, or 0.</summary>
    uint FindSpecialShopVendor(uint shopId);

    /// <summary>The scrip-exchange window is open.</summary>
    bool IsSpecialShopOpen { get; }

    /// <summary>Interact with the vendor and pick the special shop out of its options.</summary>
    bool OpenSpecialShop(uint vendorDataId, uint shopId);

    /// <summary>Buy one of an item from the open scrip window. False when it is not listed.</summary>
    bool BuyOneFromSpecialShop(uint itemId);

    void CloseSpecialShop();
}

/// <summary>The live implementation, driving <c>AgentSatisfactionSupply</c> and <c>AgentNpcTrade</c>.</summary>
public sealed unsafe class GameDeliveryWorld : IDeliveryWorld
{
    private readonly Action<string> _log;
    private readonly Dalamud.Plugin.Services.IDataManager _data;
    private readonly Dictionary<uint, HashSet<uint>> _npcShops = new();

    public GameDeliveryWorld(Dalamud.Plugin.Services.IDataManager data, Action<string> log)
    {
        _data = data;
        _log = log;
    }

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
    public int ItemCount(uint itemId, int minCollectability = 0)
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
                    if (item == null || item->ItemId != itemId) continue;
                    // Collectability shares a field with spiritbond; for a collectable it is the rating.
                    if (minCollectability > 0 && item->SpiritbondOrCollectability < minCollectability) continue;
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

    /// <summary>
    /// Which shop the open window belongs to. Read back off the agent's event receiver, so it
    /// answers the question a <c>PurchaseItem</c> step with no named shop has to ask: the vendor
    /// was interacted with and something opened — what is it, so the buy can go through its handler?
    /// </summary>
    public uint OpenShopId
    {
        get
        {
            try
            {
                var agent = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentShop.Instance();
                if (agent == null || !agent->IsAgentActive() || agent->EventReceiver == null || !agent->IsAddonReady())
                    return 0;
                var proxy = (FFXIVClientStructs.FFXIV.Client.Game.Event.ShopEventHandler.AgentProxy*)agent->EventReceiver;
                return proxy->Handler == null ? 0 : proxy->Handler->Info.EventId.Id;
            }
            catch
            {
                return 0;
            }
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

            // A step that names no shop cannot choose between several. Take the first shop the NPC
            // offers rather than guessing at the others' kinds — OpenShopId then says which it was.
            for (var i = 0; i < selector->OptionsCount; i++)
            {
                var handler = selector->Options[i].Handler;
                if (handler == null) continue;
                if (shopId != 0
                    ? handler->Info.EventId.Id != shopId
                    : handler->Info.EventId.ContentId != FFXIVClientStructs.FFXIV.Client.Game.Event.EventHandlerContent.Shop)
                    continue;
                FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance()->InteractWithHandlerFromSelector(i);
                return true;
            }
            _log(shopId != 0
                ? $"Shop {shopId:X} is not among what that NPC offers."
                : "That NPC's options include no shop.");
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

    // ── Scrip vendors ──
    //
    // A special shop is not modelled in ClientStructs the way a gil shop is: there is no agent with
    // a BuyItemIndex to set, only the ShopExchangeCurrency addon. So the purchase goes through the
    // addon's own callback, and because that shape cannot be checked without the game, the caller
    // buys one unit at a time and verifies the item actually arrived — see SpendRunner.

    private const string ScripWindow = "ShopExchangeCurrency";

    public uint FindSpecialShopVendor(uint shopId)
    {
        try
        {
            var objects = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager.Instance();
            if (objects == null) return 0;
            foreach (var obj in objects->Objects.IndexSorted)
            {
                if (obj.Value == null) continue;
                var dataId = obj.Value->BaseId;
                if (dataId == 0 || !RunsShop(dataId, shopId)) continue;
                return dataId;
            }
            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool RunsShop(uint dataId, uint shopId)
    {
        if (_npcShops.TryGetValue(dataId, out var shops)) return shops.Contains(shopId);
        try
        {
            var npc = _data.GetExcelSheet<Lumina.Excel.Sheets.ENpcBase>().GetRowOrDefault(dataId);
            var set = new HashSet<uint>();
            if (npc is { } n)
                foreach (var handler in n.ENpcData)
                    if (handler.RowId != 0)
                        set.Add(handler.RowId);
            _npcShops[dataId] = set;
            return set.Contains(shopId);
        }
        catch
        {
            return false;
        }
    }

    public bool IsSpecialShopOpen
    {
        get
        {
            try
            {
                var addon = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance()
                    ->GetAddonByName(ScripWindow);
                return addon != null && addon->IsVisible && addon->IsReady;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool OpenSpecialShop(uint vendorDataId, uint shopId) => OpenShop(vendorDataId, shopId);

    /// <summary>
    /// Buys a single unit. The index is the row in the window's own list, which is why the item is
    /// matched against <c>AtkValues</c> rather than against the sheet — a filtered or reordered list
    /// would otherwise buy the wrong thing.
    /// </summary>
    public bool BuyOneFromSpecialShop(uint itemId)
    {
        try
        {
            var addon = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance()
                ->GetAddonByName(ScripWindow);
            if (addon == null || !addon->IsVisible) return false;

            var index = IndexOf(addon, itemId);
            if (index < 0)
            {
                _log($"{itemId} is not listed in the scrip window.");
                return false;
            }

            Span<FFXIVClientStructs.FFXIV.Component.GUI.AtkValue> args =
                stackalloc FFXIVClientStructs.FFXIV.Component.GUI.AtkValue[4];
            args[0].SetInt(0);        // buy
            args[1].SetInt(index);
            args[2].SetInt(1);        // one at a time, so a mistake costs one
            args[3].SetInt(0);
            fixed (FFXIVClientStructs.FFXIV.Component.GUI.AtkValue* p = args)
                addon->FireCallback(4, p, true);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Buying {itemId} with scrips failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>The window publishes its rows as AtkValues; find the one holding this item id.</summary>
    private static int IndexOf(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, uint itemId)
    {
        for (var i = 0; i < addon->AtkValuesCount; i++)
        {
            var value = addon->AtkValues[i];
            if (value.Type != FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.UInt) continue;
            if (value.UInt != itemId) continue;
            return i;
        }
        return -1;
    }

    public void CloseSpecialShop()
    {
        try
        {
            var addon = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance()
                ->GetAddonByName(ScripWindow);
            if (addon != null && addon->IsVisible) addon->Close(false);
        }
        catch
        {
            // already gone
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
