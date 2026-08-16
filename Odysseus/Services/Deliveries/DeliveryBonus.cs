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
    /// <summary>Where <see cref="WeekRow"/> came from — for the UI to be honest about it.</summary>
    string WeekSource { get; }
}

/// <summary>
/// This week's delivery bonuses.
///
/// <para>
/// <c>SatisfactionBonusGuarantee</c> has twelve rows — one per week in the rotation — and each
/// names two client indices per route: <c>BonusDoH</c> for crafting, <c>BonusDoL</c> for
/// gathering, <c>BonusFisher</c> for fishing.
/// </para>
///
/// <para>
/// <b>The row is computed from the clock, not read from the game.</b>
/// <c>SatisfactionSupplyManager.BonusGuaranteeRowId</c> is only filled once the client has fetched
/// delivery data from the server — i.e. after the Custom Deliveries window has been opened — so
/// relying on it left the ticks blank on a fresh login. The rotation is anchored at unix
/// <c>1,657,008,000</c> (verified 2026-08-16: Tuesday 5 July 2022, 08:00 UTC — the weekly reset)
/// and advances every 604,800s, so <c>((now - anchor) / week) % 12</c> is exact from UTC alone.
/// vsatisfy does the same arithmetic but sources the time from a raw offset inside the network
/// module; that offset moves with patches, and plain UTC does not.
/// </para>
///
/// <para>
/// The game's own value is still preferred when it is loaded and in range, so anything the server
/// says wins over our arithmetic.
/// </para>
///
/// <para>A bonus route pays from a different, larger reward row — see <see cref="DeliveryRewards"/>.</para>
/// </summary>
public sealed unsafe class DeliveryBonus : IDeliveryBonus
{
    /// <summary>Tuesday 5 July 2022, 08:00 UTC — a weekly reset the rotation is anchored to.</summary>
    private const long AnchorUnix = 1_657_008_000L;
    private const long WeekSeconds = 604_800L;

    private readonly IDataManager _data;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<string>? _log;

    public DeliveryBonus(IDataManager data, Action<string>? log = null, Func<DateTimeOffset>? now = null)
    {
        _data = data;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string WeekSource { get; private set; } = "clock";

    public int WeekRow
    {
        get
        {
            var rows = RowCount;
            if (rows <= 0) return -1;

            // The game's copy, when it has actually been loaded.
            try
            {
                var manager = FFXIVClientStructs.FFXIV.Client.Game.SatisfactionSupplyManager.Instance();
                if (manager != null)
                {
                    var row = manager->BonusGuaranteeRowId;
                    if (row < rows)
                    {
                        WeekSource = "game";
                        return row;
                    }
                }
            }
            catch
            {
                // fall through to the clock
            }

            WeekSource = "clock";
            return ComputeRow(_now().ToUnixTimeSeconds(), rows);
        }
    }

    /// <summary>The rotation index for a moment in time. Pure, so the anchor can be pinned by a test.</summary>
    public static int ComputeRow(long unixSeconds, int rowCount)
    {
        if (rowCount <= 0) return -1;
        var weeks = (unixSeconds - AnchorUnix) / WeekSeconds;
        var row = weeks % rowCount;
        return (int)(row < 0 ? row + rowCount : row);
    }

    private int RowCount
    {
        get
        {
            try
            {
                return (int)_data.GetExcelSheet<SatisfactionBonusGuarantee>().Count;
            }
            catch
            {
                return 0;
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
