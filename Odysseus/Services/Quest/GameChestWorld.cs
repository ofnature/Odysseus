using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Odysseus.Services.Quest;

/// <summary>
/// The live <see cref="IChestWorld"/>. A translation layer with no decisions in it: which stack to
/// take and when to stop live in <see cref="ChestWithdrawer"/>, where they can be tested.
/// </summary>
public sealed unsafe class GameChestWorld : IChestWorld
{
    private const string ChestAddon = "FreeCompanyChest";

    /// <summary>The five chest pages, in tab order.</summary>
    private static readonly InventoryType[] Pages =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5,
    ];

    private static readonly InventoryType[] Bags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private readonly Dalamud.Plugin.Services.IGameGui _gui;
    private readonly Dalamud.Plugin.Services.IDataManager _data;
    private readonly Func<uint, int> _held;
    private readonly Action<string> _log;

    /// <summary>
    /// The split move's return slot and four values. They outlive the call on purpose: the "how
    /// many?" prompt reads the move back when it is answered, after the call has returned
    /// (QuickTransfer keeps its buffers alive for the same reason). One move runs at a time, so one
    /// buffer, allocated once, does.
    /// </summary>
    private readonly AtkValue* _moveValues = (AtkValue*)System.Runtime.InteropServices.NativeMemory.AllocZeroed(5, (nuint)sizeof(AtkValue));

    public GameChestWorld(Dalamud.Plugin.Services.IGameGui gui, Dalamud.Plugin.Services.IDataManager data,
        Func<uint, int> held, Action<string> log)
    {
        _gui = gui;
        _data = data;
        _held = held;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public bool ChestOpen
    {
        get
        {
            try
            {
                var addon = _gui.GetAddonByName(ChestAddon);
                return !addon.IsNull && addon.IsVisible;
            }
            catch
            {
                return false;
            }
        }
    }

    public int Held(uint itemId) => _held(itemId);

    public IReadOnlyList<ChestStack> ChestStacks(uint itemId)
    {
        var stacks = new List<ChestStack>();
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return stacks;

            foreach (var page in Pages)
            {
                var container = manager->GetInventoryContainer(page);
                // A page loads when its tab is first viewed; an unviewed one reads as empty.
                if (container == null || !container->IsLoaded) continue;
                for (var slot = 0; slot < container->Size; slot++)
                {
                    var item = container->GetInventorySlot(slot);
                    if (item == null || item->ItemId != itemId || item->Quantity == 0) continue;
                    stacks.Add(new ChestStack((int)page, (short)slot, itemId, (int)item->Quantity));
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Reading the chest for item {itemId} failed: {ex.Message}");
        }
        return stacks;
    }

    /// <summary>
    /// Move a whole stack into the bags: into a same-item stack that can take all of it, else the
    /// first empty slot. A strict fit only — a partial merge would leave a remainder behind that
    /// the caller's "did the slot empty" check would then read as a failure.
    /// </summary>
    public bool Withdraw(ChestStack stack)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return false;

            var flags = FlagsOf((InventoryType)stack.Container, stack.Slot);
            if (Destination(manager, stack, flags, out var type, out var slot) is false)
                return false;

            manager->MoveItemSlot((InventoryType)stack.Container, (ushort)stack.Slot, type, (ushort)slot, true);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Withdrawing item {stack.ItemId} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Part of a stack, the way a player does it: <c>RaptureAtkModule.HandleItemMove</c> with
    /// [source inventory type, source slot, destination type, destination slot] — the move that a
    /// click on the stack then a click on a bag slot makes (QuickTransfer's route, read for Charon's
    /// FC chest plan). The game then asks how many; the withdrawer answers.
    /// </summary>
    public bool WithdrawSome(ChestStack stack, int amount)
    {
        try
        {
            var manager = InventoryManager.Instance();
            var module = RaptureAtkModule.Instance();
            if (manager == null || module == null) return false;

            var flags = FlagsOf((InventoryType)stack.Container, stack.Slot);
            if (Destination(manager, stack with { Quantity = amount }, flags, out var type, out var slot) is false)
                return false;

            var values = _moveValues + 1;
            values[0].SetInt(stack.Container);
            values[1].SetInt(stack.Slot);
            values[2].SetInt((int)type);
            values[3].SetInt(slot);
            module->HandleItemMove(_moveValues, values, 4);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Withdrawing {amount} of item {stack.ItemId} failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    public bool QuantityPromptOpen
    {
        get
        {
            try
            {
                var addon = _gui.GetAddonByName("InputNumeric");
                return !addon.IsNull && addon.IsVisible;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>OK with this many — the prompt's own callback, recorded as <c>[Int n]</c> (Cancel is <c>-1</c>).</summary>
    public void AnswerQuantity(int amount)
    {
        try
        {
            var unit = (AtkUnitBase*)_gui.GetAddonByName("InputNumeric").Address;
            if (unit == null) return;
            var value = stackalloc AtkValue[1];
            value[0].SetInt(amount);
            unit->FireCallback(1, value, true);
        }
        catch (Exception ex)
        {
            _log($"Answering the chest's quantity prompt failed: {ex.Message}");
        }
    }

    /// <summary>
    /// What is left in the stack's slot. Success is it emptying, or shrinking by what was asked for;
    /// the return code cannot be used — Charon verified it comes back 6 on moves that plainly worked.
    /// </summary>
    public int QuantityAt(ChestStack stack)
    {
        try
        {
            var manager = InventoryManager.Instance();
            if (manager == null) return 0;
            var container = manager->GetInventoryContainer((InventoryType)stack.Container);
            if (container == null || !container->IsLoaded || stack.Slot >= container->Size) return 0;
            var item = container->GetInventorySlot(stack.Slot);
            return item == null || item->ItemId != stack.ItemId ? 0 : (int)item->Quantity;
        }
        catch
        {
            return 0; // unreadable mid-transition; the caller's own count is the backstop
        }
    }

    public void Log(string message) => _log(message);

    private bool Destination(InventoryManager* manager, ChestStack stack,
        InventoryItem.ItemFlags? flags, out InventoryType type, out int slot)
    {
        foreach (var bag in Bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item == null) continue;
                if (item->ItemId != stack.ItemId || item->Quantity == 0 || flags is null || item->Flags != flags.Value)
                    continue;
                // Merge only when the whole source stack fits.
                if (item->Quantity + stack.Quantity > MaxStack(stack.ItemId)) continue;
                type = bag;
                slot = i;
                return true;
            }
        }

        foreach (var bag in Bags)
        {
            var container = manager->GetInventoryContainer(bag);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var item = container->GetInventorySlot(i);
                if (item != null && item->ItemId == 0)
                {
                    type = bag;
                    slot = i;
                    return true;
                }
            }
        }

        type = InventoryType.Inventory1;
        slot = -1;
        return false;
    }

    /// <summary>Stack ceiling for an item; 1 when it cannot be read, so nothing ever merges blindly.</summary>
    private uint MaxStack(uint itemId)
    {
        try
        {
            return _data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId)?.StackSize ?? 1u;
        }
        catch
        {
            return 1;
        }
    }

    private static InventoryItem.ItemFlags? FlagsOf(InventoryType container, short slot)
    {
        try
        {
            var inv = InventoryManager.Instance()->GetInventoryContainer(container);
            if (inv == null || !inv->IsLoaded || slot >= inv->Size) return null;
            var item = inv->GetInventorySlot(slot);
            return item == null ? null : item->Flags;
        }
        catch
        {
            return null;
        }
    }
}
