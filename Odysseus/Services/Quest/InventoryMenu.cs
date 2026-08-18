using System;
using System.Collections.Generic;
using Dalamud.Memory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Quest;

/// <summary>One stack of an item sitting in a player bag.</summary>
public readonly record struct BagStack(InventoryType Container, int Slot, int Quantity);

/// <summary>
/// The item context menu — the only route to selling.
///
/// <para>
/// <c>ShopEventHandler</c> has <c>ExecuteBuy</c> and no sell at all, so a vendor sale cannot go
/// through the shop the way a purchase does; it goes through the inventory context menu, the way
/// SimpleTweaks QuickSellItems and DailyRoutines AutoSplitStacks drive it. Ported from Charon,
/// where this exact code has been in production behind the gil-cap seller and the Doman donator.
/// </para>
///
/// <para>
/// Two preconditions, both learned from in-game failures rather than from reading:
/// the inventory window must be <b>visible</b> or <c>OpenForItemSlot</c> silently does nothing —
/// and an unattended character never has it open — and the menu then needs a frame to build, so
/// opening and clicking cannot happen in the same tick.
/// </para>
///
/// <para>
/// Entries are matched by Addon-sheet text, so both sides of the comparison are localised and the
/// match holds in any client language.
/// </para>
/// </summary>
public static unsafe class InventoryMenu
{
    /// <summary>Addon sheet row 93 — "Sell".</summary>
    public const uint SellTextRow = 93;

    public static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    /// <summary>Every stack of an item in the player bags, live.</summary>
    public static List<BagStack> FindStacks(uint itemId)
    {
        var stacks = new List<BagStack>();
        var manager = InventoryManager.Instance();
        if (manager == null)
            return stacks;

        foreach (var bag in PlayerBags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded)
                continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->GetBaseItemId() == itemId && slot->GetQuantity() > 0)
                    stacks.Add(new BagStack(bag, i, (int)slot->GetQuantity()));
            }
        }
        return stacks;
    }

    /// <summary>Outcome of one click attempt on the open menu.</summary>
    public enum ClickResult
    {
        /// <summary>The menu is not up yet — try again next tick.</summary>
        NotReady,
        /// <summary>Entry found and clicked; the menu was closed.</summary>
        Clicked,
        /// <summary>The menu is up but has no such entry; the menu was closed.</summary>
        EntryMissing,
    }

    /// <summary>
    /// Ask the game to open a stack's context menu. False means the inventory window was not up
    /// and has been asked to open — call again next tick.
    /// </summary>
    public static bool OpenMenu(BagStack stack)
    {
        var agent = AgentInventoryContext.Instance();
        var inventoryAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (agent == null || inventoryAgent == null || !EnsureInventoryOpen())
            return false;

        agent->OpenForItemSlot(stack.Container, stack.Slot, 0, inventoryAgent->GetAddonId());
        return true;
    }

    /// <summary>The inventory window must be visible for item operations to land.</summary>
    public static bool EnsureInventoryOpen()
    {
        var inventoryAgent = AgentModule.Instance()->GetAgentByInternalId(AgentId.Inventory);
        if (inventoryAgent == null)
            return false;

        var addonId = inventoryAgent->GetAddonId();
        var addon = addonId == 0 ? null : RaptureAtkUnitManager.Instance()->GetAddonById((ushort)addonId);

        if (addon == null)
        {
            inventoryAgent->Show(); // never opened this session
            return false;
        }
        if (!addon->IsVisible)
        {
            addon->Open(1);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Split a quantity off a bag stack into a fresh one. Own-bag splits go through
    /// <c>InventoryManager.SplitItem</c> directly and are silent — no quantity prompt is involved,
    /// which is the same call the FC chest seed-return uses.
    /// </summary>
    public static int SplitStack(BagStack stack, int quantity)
        => InventoryManager.Instance()->SplitItem(stack.Container, (ushort)stack.Slot, quantity);

    /// <summary>
    /// Click the open menu's entry whose text is the given Addon-sheet row. Call each tick after
    /// <see cref="OpenMenu"/> until it stops returning <see cref="ClickResult.NotReady"/>. On any
    /// terminal outcome the menu is closed — a stray context menu must never be left on screen.
    /// </summary>
    public static ClickResult TryClickEntry(IDataManager data, uint addonTextRow)
    {
        var wanted = data.GetExcelSheet<Addon>()?.GetRowOrDefault(addonTextRow)?.Text.ExtractText();
        if (string.IsNullOrEmpty(wanted))
            return ClickResult.EntryMissing;

        var agent = AgentInventoryContext.Instance();
        if (agent == null || !agent->AgentInterface.IsAgentActive())
            return ClickResult.NotReady;

        var contextAddonId = agent->AgentInterface.GetAddonId();
        var contextAddon = contextAddonId == 0
            ? null
            : RaptureAtkUnitManager.Instance()->GetAddonById((ushort)contextAddonId);
        if (contextAddon == null || !contextAddon->IsVisible)
            return ClickResult.NotReady;

        var clicked = false;
        for (var i = 0; i < agent->ContextItemCount; i++)
        {
            // ClientStructs really does spell it ContexItemStartIndex.
            var param = agent->EventParams[agent->ContexItemStartIndex + i];
            if (param.Type != AtkValueType.String)
                continue;
            var text = MemoryHelper.ReadSeStringNullTerminated((nint)param.String.Value).TextValue;
            if (!string.Equals(text, wanted, StringComparison.Ordinal))
                continue;

            var values = stackalloc AtkValue[5];
            values[0].SetInt(0);
            values[1].SetInt(i);
            values[2].SetUInt(0);
            values[3].SetInt(0);
            values[4].SetInt(0);
            contextAddon->FireCallback(5, values, true);
            clicked = true;
            break;
        }

        agent->AgentInterface.Hide();
        contextAddon->Close(false);
        return clicked ? ClickResult.Clicked : ClickResult.EntryMissing;
    }
}
