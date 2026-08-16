using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Odysseus.Services.Tribes;

/// <summary>What a tribe's dailies ask of the character.</summary>
public enum TribeKind
{
    Combat,
    Crafter,
    Gatherer,
    /// <summary>Namazu: land or hand.</summary>
    Mixed,
    Unknown,
}

/// <summary>Where a tribe's dailies are handed out.</summary>
public sealed record TribeIssuer(uint ENpcId, uint TerritoryId, Vector3 Position, int DailyCount);

/// <summary>One allied society, as the sheets describe it.</summary>
/// <param name="UnlockQuestId">
/// The society's first non-repeatable quest — completing it (and its prerequisites) opens the
/// dailies. Amalj'aa's is "Brotherhood of Ash", which itself needs "Peace for Thanalan".
/// </param>
/// <param name="IconId">The society's emblem, for the UI. 0 when the sheet has none.</param>
public sealed record TribeInfo(
    byte Id, string Name, uint ExpansionId, TribeKind Kind, byte MaxRank,
    IReadOnlyList<TribeIssuer> Issuers, IReadOnlyList<ushort> DailyQuestIds, ushort UnlockQuestId = 0, uint IconId = 0)
{
    /// <summary>The issuer with the most dailies — where to stand. ARR tribes have three (one per rank band); later ones have one.</summary>
    public TribeIssuer? PrimaryIssuer => Issuers.OrderByDescending(i => i.DailyCount).FirstOrDefault();

    /// <summary>The kinds Odysseus can run today: combat only, until Craft/Gather handoffs exist.</summary>
    public bool IsRunnableKind => Kind == TribeKind.Combat;
}

/// <summary>
/// The twenty allied societies, read once from the game's own sheets — nothing hand-typed.
///
/// <para>
/// Verified 2026-08-15: every daily is a <c>Quest</c> row with <c>BeastTribe</c> set and
/// <c>IsRepeatable</c>; its <c>IssuerStart</c> is the ENpc that hands it out and
/// <c>IssuerLocation</c> a <c>Level</c> row with territory and position; its
/// <c>ClassJobCategory0</c> says combat / hand / land. Eleven tribes are combat, five crafter,
/// three gatherer, one mixed. That measurement is why P7 starts with combat.
/// </para>
/// </summary>
public sealed class TribeCatalog
{
    private readonly Dictionary<byte, TribeInfo> _byId = new();

    public TribeCatalog(IDataManager data, Action<string> log)
    {
        try
        {
            var quests = data.GetExcelSheet<Lumina.Excel.Sheets.Quest>();
            var dailiesByTribe = quests
                .Where(q => q.RowId >= 65536 && q.IsRepeatable && q.BeastTribe.RowId != 0)
                .GroupBy(q => (byte)q.BeastTribe.RowId)
                .ToDictionary(g => g.Key, g => g.ToList());
            // The story/rank quests: same tribe, not repeatable. The lowest id is the one that opens it.
            var unlockByTribe = quests
                .Where(q => q.RowId >= 65536 && !q.IsRepeatable && q.BeastTribe.RowId != 0)
                .GroupBy(q => (byte)q.BeastTribe.RowId)
                .ToDictionary(g => g.Key, g => (ushort)(g.Min(q => q.RowId) - 65536));

            foreach (var tribe in data.GetExcelSheet<BeastTribe>())
            {
                if (tribe.RowId == 0 || tribe.RowId > byte.MaxValue) continue;
                var name = tribe.Name.ExtractText();
                if (name.Length == 0) continue;
                dailiesByTribe.TryGetValue((byte)tribe.RowId, out var dailies);
                dailies ??= [];

                var issuers = dailies
                    .Where(q => q.IssuerStart.RowId != 0)
                    .GroupBy(q => q.IssuerStart.RowId)
                    .Select(g =>
                    {
                        var level = g.Select(q => q.IssuerLocation.ValueNullable).FirstOrDefault(l => l is not null);
                        return new TribeIssuer(g.Key, level?.Territory.RowId ?? 0,
                            level is { } l ? new Vector3(l.X, l.Y, l.Z) : Vector3.Zero, g.Count());
                    })
                    .ToList();

                var kind = KindOf(dailies);
                unlockByTribe.TryGetValue((byte)tribe.RowId, out var unlock);
                _byId[(byte)tribe.RowId] = new TribeInfo(
                    (byte)tribe.RowId, Capitalise(name), tribe.Expansion.RowId, kind, tribe.MaxRank,
                    issuers, dailies.Select(q => (ushort)(q.RowId - 65536)).ToList(), unlock,
                    tribe.IconReputation != 0 ? tribe.IconReputation : tribe.Icon);
            }
        }
        catch (Exception ex)
        {
            log($"Tribe catalog failed to load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Test constructor.</summary>
    public TribeCatalog(IEnumerable<TribeInfo> tribes)
    {
        foreach (var t in tribes) _byId[t.Id] = t;
    }

    public IReadOnlyCollection<TribeInfo> All => _byId.Values;

    public TribeInfo? ById(byte id) => _byId.TryGetValue(id, out var t) ? t : null;

    /// <summary>The tribe a daily quest belongs to, or null.</summary>
    public TribeInfo? ByDaily(ushort questId) => _byId.Values.FirstOrDefault(t => t.DailyQuestIds.Contains(questId));

    private static TribeKind KindOf(List<Lumina.Excel.Sheets.Quest> dailies)
    {
        var names = dailies.Select(q => q.ClassJobCategory0.ValueNullable?.Name.ExtractText() ?? "").Distinct().ToList();
        if (names.Count == 0) return TribeKind.Unknown;
        var war = names.Any(n => n.Contains("War", StringComparison.OrdinalIgnoreCase) || n.Contains("Magic", StringComparison.OrdinalIgnoreCase));
        var hand = names.Any(n => n.Contains("Hand", StringComparison.OrdinalIgnoreCase));
        var land = names.Any(n => n.Contains("Land", StringComparison.OrdinalIgnoreCase));
        if (war) return TribeKind.Combat;
        if (hand && land) return TribeKind.Mixed;
        if (hand) return TribeKind.Crafter;
        if (land) return TribeKind.Gatherer;
        return TribeKind.Unknown;
    }

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
