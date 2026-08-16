using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>Something a scrip vendor sells.</summary>
/// <param name="Currency">Reward-currency index of the scrip it costs — matches <see cref="ScripKind.RewardCurrency"/>.</param>
/// <param name="Cost">Scrips per unit.</param>
/// <param name="IsBook">A master recipe tome or folklore book — learned once and then worthless.</param>
/// <param name="ShopId">
/// The <c>SpecialShop</c> row, which is also the event-handler id an NPC exposes it under.
/// </param>
public sealed record ScripOffer(uint ItemId, string Name, int Currency, int Cost, bool IsBook, uint ShopId = 0)
{
    public override string ToString() => $"{Name} ({Cost:N0})";
}

/// <summary>What the scrip vendors stock.</summary>
public interface IScripShop
{
    IReadOnlyList<ScripOffer> Offers { get; }
    /// <summary>The book has already been read — buying it again would be waste.</summary>
    bool IsLearned(ScripOffer offer);
}

/// <summary>
/// Scrip vendor stock, read from <c>SpecialShop</c>.
///
/// <para>
/// A special shop row pairs what you receive with what you give; when the thing given is one of the
/// scrip items, the row is a scrip purchase and the pair is (item, price). That is the whole
/// derivation — there is no "scrip vendor" sheet, only shops that happen to take scrips.
/// </para>
///
/// <para>
/// Master recipe tomes are singled out because they are the one purchase that terminates: a tome
/// teaches its recipes once and is dead weight afterwards, so "buy the ones I lack" is a spend rule
/// that empties itself rather than running forever. They are identified from
/// <c>SecretRecipeBook.Item</c> — the sheet maps book to item directly, so no <c>ItemAction</c>
/// type constant has to be guessed — and checked with <c>PlayerState.IsSecretRecipeBookUnlocked</c>.
/// </para>
///
/// <para>
/// <b>Gathering folklore tomes are not detected.</b> <c>PlayerState.IsFolkloreBookUnlocked</c>
/// exists but nothing in the sheets maps an item to a folklore id, so deciding which offers are
/// folklore would mean guessing an <c>ItemAction</c> type — and being wrong there spends scrips on
/// something already owned. They can still be bought by ticking them in the ordinary list.
/// </para>
/// </summary>
public sealed unsafe class ScripShop : IScripShop
{
    private readonly List<ScripOffer> _offers = [];
    /// <summary>Item id → <c>SecretRecipeBook</c> row, which is what the unlock check takes.</summary>
    private readonly Dictionary<uint, uint> _books = new();
    private readonly Action<string>? _log;

    public ScripShop(IDataManager data, IEnumerable<ScripKind> scrips, Action<string>? log = null)
    {
        _log = log;
        try
        {
            foreach (var book in data.GetExcelSheet<SecretRecipeBook>())
                if (book.Item.RowId != 0)
                    _books.TryAdd(book.Item.RowId, book.RowId);

            var byItem = scrips.ToDictionary(s => s.ItemId, s => s.RewardCurrency);
            var seen = new HashSet<(uint, int)>();

            foreach (var shop in data.GetExcelSheet<SpecialShop>())
            {
                foreach (var entry in shop.Item)
                {
                    // What it costs. A scrip purchase gives exactly one kind of scrip.
                    var currency = 0;
                    var cost = 0;
                    foreach (var give in entry.ItemCosts)
                    {
                        if (give.ItemCost.RowId == 0 || !byItem.TryGetValue(give.ItemCost.RowId, out var kind)) continue;
                        currency = kind;
                        cost = (int)give.CurrencyCost;
                        break;
                    }
                    if (currency == 0 || cost <= 0) continue;

                    foreach (var receive in entry.ReceiveItems)
                    {
                        var item = receive.Item.ValueNullable;
                        if (item is not { } got || got.RowId == 0) continue;
                        if (!seen.Add((got.RowId, currency))) continue;

                        _offers.Add(new ScripOffer(got.RowId, got.Name.ExtractText(), currency, cost,
                            _books.ContainsKey(got.RowId), shop.RowId));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Scrip shop failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test constructor.</summary>
    public ScripShop(IEnumerable<ScripOffer> offers, Func<uint, bool>? learned = null)
    {
        _offers.AddRange(offers);
        _learnedOverride = learned;
    }

    private readonly Func<uint, bool>? _learnedOverride;

    public IReadOnlyList<ScripOffer> Offers => _offers;

    /// <summary>
    /// Whether the book has been read. Unreadable counts as <b>learned</b> on purpose: refusing to
    /// buy something you might already own wastes nothing, while the opposite wastes scrips.
    /// </summary>
    public bool IsLearned(ScripOffer offer)
    {
        if (!offer.IsBook) return false;
        if (_learnedOverride is not null) return _learnedOverride(offer.ItemId);
        try
        {
            if (!_books.TryGetValue(offer.ItemId, out var book)) return true;
            var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
            return state == null || state->IsSecretRecipeBookUnlocked(book);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not tell whether {offer.Name} is learned: {ex.Message}");
            return true;
        }
    }
}
