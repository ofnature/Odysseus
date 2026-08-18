using System;
using System.Collections.Generic;

namespace Odysseus.Services.Quest;

/// <summary>
/// The live <see cref="ISellWorld"/> — a translation layer over <see cref="InventoryMenu"/> and
/// the shop window, with no decisions in it. Everything that decides lives in
/// <see cref="RewardSeller"/> where it can be tested without a client.
/// </summary>
public sealed class GameSellWorld : ISellWorld
{
    private readonly Dalamud.Plugin.Services.IDataManager _data;
    private readonly Func<bool> _shopOpen;
    private readonly Func<uint, int> _held;
    private readonly Action<string> _log;

    /// <param name="shopOpen">Whether a vendor window is up — the delivery world already answers this.</param>
    /// <param name="held">Bag count for an item.</param>
    public GameSellWorld(Dalamud.Plugin.Services.IDataManager data, Func<bool> shopOpen, Func<uint, int> held,
        Action<string> log)
    {
        _data = data;
        _shopOpen = shopOpen;
        _held = held;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    public bool ShopOpen => _shopOpen();

    public int Held(uint itemId) => _held(itemId);

    public IReadOnlyList<BagStack> Stacks(uint itemId) => InventoryMenu.FindStacks(itemId);

    public bool Split(BagStack stack, int quantity)
    {
        try
        {
            if (!InventoryMenu.EnsureInventoryOpen())
                return true; // asked for the window; the caller retries rather than treating this as refusal
            InventoryMenu.SplitStack(stack, quantity);
            return true;
        }
        catch (Exception ex)
        {
            _log($"Splitting {quantity} off a stack failed: {ex.Message}");
            return false;
        }
    }

    public bool OpenMenu(BagStack stack)
    {
        try
        {
            return InventoryMenu.OpenMenu(stack);
        }
        catch (Exception ex)
        {
            _log($"Opening the item menu failed: {ex.Message}");
            return false;
        }
    }

    public InventoryMenu.ClickResult ClickSell()
    {
        try
        {
            return InventoryMenu.TryClickEntry(_data, InventoryMenu.SellTextRow);
        }
        catch (Exception ex)
        {
            _log($"Clicking Sell failed: {ex.Message}");
            return InventoryMenu.ClickResult.EntryMissing;
        }
    }

    public void Log(string message) => _log(message);
}
