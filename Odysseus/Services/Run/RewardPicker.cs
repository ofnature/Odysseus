using System;
using System.Collections.Generic;
using System.Linq;

namespace Odysseus.Services.Run;

/// <summary>One way of choosing an optional quest reward; the player orders them.</summary>
public enum RewardRule
{
    /// <summary>The gear coffer for your role — "… of Fending Coffer" for a tank, and so on.</summary>
    GearCoffer,
    /// <summary>A high-quality piece of equipment.</summary>
    HighQualityGear,
    /// <summary>The one that sells to a vendor for the most (price × amount).</summary>
    HighestVendorValue,
    /// <summary>Gil.</summary>
    GilSacks,
    /// <summary>Equipment the current job can wear.</summary>
    EquippableForJob,
}

/// <summary>One optional reward as the reward window offers it.</summary>
public sealed record RewardOption(int Index, uint ItemId, bool Hq, uint Amount, string Name);

/// <summary>What the rules need to know about an item and the character.</summary>
/// <param name="JobAbbreviation">The current job, "WAR", "WHM" …</param>
/// <param name="CanEquip">The current job can wear this item.</param>
/// <param name="IsEquipment">The item goes in an equipment slot at all.</param>
/// <param name="VendorPrice">What a vendor pays for one.</param>
public sealed record RewardFacts(string JobAbbreviation, Func<uint, bool> CanEquip, Func<uint, bool> IsEquipment, Func<uint, uint> VendorPrice);

/// <summary>
/// Chooses an optional quest reward: the first rule, in the player's order, that any reward
/// matches; the first reward when none does. Odysseus's own — TextAdvance publishes no licence,
/// so its picker is not copied; these are the same five choices it offers, decided from the
/// game's sheets (gil is item 1, equipment has a slot, a job's gear its job category).
/// </summary>
public static class RewardPicker
{
    /// <summary>The order a new install starts with — TextAdvance's own default, which players know.</summary>
    public static readonly RewardRule[] DefaultOrder =
        [RewardRule.GearCoffer, RewardRule.HighQualityGear, RewardRule.HighestVendorValue, RewardRule.GilSacks, RewardRule.EquippableForJob];

    /// <summary>A saved order made whole: each rule once, any missing ones after, in the default order.</summary>
    public static IReadOnlyList<RewardRule> Normalise(IEnumerable<RewardRule>? saved)
    {
        var order = (saved ?? []).Where(r => Enum.IsDefined(r)).Distinct().ToList();
        order.AddRange(DefaultOrder.Where(r => !order.Contains(r)));
        return order;
    }

    public static string Describe(RewardRule rule) => rule switch
    {
        RewardRule.GearCoffer => "Gear coffer for my role",
        RewardRule.HighQualityGear => "High-quality gear",
        RewardRule.HighestVendorValue => "Highest vendor value",
        RewardRule.GilSacks => "Gil",
        RewardRule.EquippableForJob => "Gear my job can wear",
        _ => rule.ToString(),
    };

    /// <summary>The reward to take and why; null only when there is nothing to choose from.</summary>
    public static (RewardOption Option, string Why)? Choose(IReadOnlyList<RewardOption> options, IEnumerable<RewardRule> order, RewardFacts facts)
    {
        if (options.Count == 0)
            return null;
        foreach (var rule in order)
            if (Match(rule, options, facts) is { } hit)
                return (hit, Describe(rule).ToLowerInvariant());
        return (options[0], "the first — no rule matched");
    }

    private static RewardOption? Match(RewardRule rule, IReadOnlyList<RewardOption> options, RewardFacts facts) => rule switch
    {
        RewardRule.GilSacks => options.FirstOrDefault(o => o.ItemId == 1),
        RewardRule.GearCoffer => CofferFor(facts.JobAbbreviation, options),
        RewardRule.HighQualityGear => options.FirstOrDefault(o => o.Hq && facts.IsEquipment(o.ItemId)),
        RewardRule.EquippableForJob => options.FirstOrDefault(o => facts.IsEquipment(o.ItemId) && facts.CanEquip(o.ItemId)),
        RewardRule.HighestVendorValue => options
            .Select(o => (Option: o, Value: (ulong)facts.VendorPrice(o.ItemId) * Math.Max(1u, o.Amount)))
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .Select(x => x.Option)
            .FirstOrDefault(),
        _ => null,
    };

    /// <summary>
    /// The role coffer for this job. The sheets carry no role for a coffer — every one has the same
    /// icon, category and action — so it is read off the name: "Hellfire Armor of Fending Coffer".
    /// The words are the English client's; on another language this rule simply never matches.
    /// </summary>
    private static RewardOption? CofferFor(string job, IReadOnlyList<RewardOption> options)
    {
        if (RoleWords(job) is not { } words)
            return null;   // a crafter or gatherer has no role coffer
        return options.FirstOrDefault(o => o.Name.Contains("Coffer", StringComparison.OrdinalIgnoreCase)
            && words.Any(w => o.Name.Contains($" of {w}", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The coffer words for a job: its armour's, then its accessories'.</summary>
    public static string[]? RoleWords(string job) => job.ToUpperInvariant() switch
    {
        "GLA" or "PLD" or "MRD" or "WAR" or "DRK" or "GNB" => ["Fending"],
        "CNJ" or "WHM" or "SCH" or "AST" or "SGE" => ["Healing"],
        "THM" or "BLM" or "ACN" or "SMN" or "RDM" or "PCT" or "BLU" => ["Casting"],
        "LNC" or "DRG" or "RPR" => ["Maiming", "Slaying"],
        "PGL" or "MNK" or "SAM" => ["Striking", "Slaying"],
        "ROG" or "NIN" or "VPR" => ["Scouting", "Aiming"],
        "ARC" or "BRD" or "MCH" or "DNC" => ["Aiming"],
        _ => null,
    };
}
