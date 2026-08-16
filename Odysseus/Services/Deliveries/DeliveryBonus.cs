using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Deliveries;

/// <summary>The three turn-in routes a client offers. Slot numbers match <c>SatisfactionSupply.Slot</c>.</summary>
public enum DeliveryRoute
{
    Craft = 1,
    Gather = 2,
    Fish = 3,
}

/// <summary>Which of a client's routes carry this week's bonus.</summary>
public sealed record BonusFlags(bool Craft, bool Gather, bool Fish)
{
    public bool this[DeliveryRoute route] => route switch
    {
        DeliveryRoute.Craft => Craft,
        DeliveryRoute.Gather => Gather,
        _ => Fish,
    };

    public static BonusFlags None { get; } = new(false, false, false);
}

/// <summary>Reads which clients have a bonus this week.</summary>
public interface IDeliveryBonus
{
    BonusFlags For(DeliveryClient client);
    /// <summary>Which of the twelve weekly rows is live, or -1 when unreadable.</summary>
    int WeekRow { get; }
}

/// <summary>
/// This week's delivery bonuses.
///
/// <para>
/// <c>SatisfactionBonusGuarantee</c> has twelve rows — one per week in the rotation — and each
/// names two client indices per route: <c>BonusDoH</c> for crafting, <c>BonusDoL</c> for
/// gathering, <c>BonusFisher</c> for fishing. The live row is
/// <c>SatisfactionSupplyManager.BonusGuaranteeRowId</c>, so no clock arithmetic is needed (vsatisfy
/// computes it from device time because it also wants to predict future weeks; we only need now).
/// </para>
///
/// <para>A bonus route pays from a different, larger reward row — see <see cref="DeliveryRewards"/>.</para>
/// </summary>
public sealed unsafe class DeliveryBonus : IDeliveryBonus
{
    private readonly IDataManager _data;
    private readonly Action<string>? _log;

    public DeliveryBonus(IDataManager data, Action<string>? log = null)
    {
        _data = data;
        _log = log;
    }

    public int WeekRow
    {
        get
        {
            try
            {
                var manager = FFXIVClientStructs.FFXIV.Client.Game.SatisfactionSupplyManager.Instance();
                if (manager == null) return -1;
                var row = manager->BonusGuaranteeRowId;
                return row == byte.MaxValue ? -1 : row;
            }
            catch
            {
                return -1;
            }
        }
    }

    public BonusFlags For(DeliveryClient client)
    {
        try
        {
            var week = WeekRow;
            if (week < 0) return BonusFlags.None;
            var row = _data.GetExcelSheet<SatisfactionBonusGuarantee>().GetRowOrDefault((uint)week);
            if (row is not { } r) return BonusFlags.None;
            var index = (int)client.Index;
            return new BonusFlags(
                r.BonusDoH.Any(x => x == index),
                r.BonusDoL.Any(x => x == index),
                r.BonusFisher.Any(x => x == index));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Bonus read failed: {ex.Message}");
            return BonusFlags.None;
        }
    }
}
