using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class RewardPickerTests
{
    // 1 gil · 2 a tank's coffer · 3 a healer's coffer · 4 HQ sword (GLA can wear) · 5 a pricey crystal
    private static readonly RewardOption Gil = new(0, 1, false, 500, "Gil");
    private static readonly RewardOption TankCoffer = new(1, 20601, false, 1, "Hellfire Armor of Fending Coffer (IL 240)");
    private static readonly RewardOption HealerCoffer = new(2, 20606, false, 1, "Hailstorm Attire of Healing Coffer (IL 240)");
    private static readonly RewardOption HqSword = new(3, 4000, true, 1, "Bronze Gladius");
    private static readonly RewardOption Crystal = new(4, 5000, false, 10, "Fire Cluster");

    private static RewardFacts Facts(string job) => new(job,
        id => id == 4000 && job is "GLA" or "PLD",
        id => id == 4000,
        id => id switch { 4000 => 100u, 5000 => 30u, _ => 0u });

    [Fact]
    public void The_first_rule_that_matches_wins()
    {
        RewardOption[] all = [Gil, TankCoffer, HealerCoffer, HqSword, Crystal];
        Assert.Equal(TankCoffer, RewardPicker.Choose(all, RewardPicker.DefaultOrder, Facts("PLD"))!.Value.Option);
        Assert.Equal(HealerCoffer, RewardPicker.Choose(all, RewardPicker.DefaultOrder, Facts("WHM"))!.Value.Option);
        Assert.Equal(Gil, RewardPicker.Choose(all, [RewardRule.GilSacks, RewardRule.GearCoffer], Facts("PLD"))!.Value.Option);
    }

    /// <summary>A coffer for another role is never taken for its own sake; a crafter has none.</summary>
    [Fact]
    public void A_coffer_for_another_role_does_not_match()
    {
        RewardOption[] offered = [HealerCoffer, Crystal];
        var pick = RewardPicker.Choose(offered, [RewardRule.GearCoffer, RewardRule.HighestVendorValue], Facts("WAR"))!.Value;
        Assert.Equal(Crystal, pick.Option);   // 30 × 10 beats nothing
        Assert.Null(RewardPicker.RoleWords("CRP"));
    }

    [Fact]
    public void Vendor_value_counts_the_amount_and_nothing_matching_takes_the_first()
    {
        RewardOption[] offered = [HqSword, Crystal];
        Assert.Equal(Crystal, RewardPicker.Choose(offered, [RewardRule.HighestVendorValue], Facts("WHM"))!.Value.Option);   // 300 > 100
        var none = RewardPicker.Choose([Crystal, Gil], [RewardRule.EquippableForJob], Facts("WHM"))!.Value;
        Assert.Equal(Crystal, none.Option);
        Assert.Contains("no rule matched", none.Why);
    }

    /// <summary>A saved order is made whole: duplicates dropped, missing rules appended in the default order.</summary>
    [Fact]
    public void A_saved_order_is_normalised()
    {
        var order = RewardPicker.Normalise([RewardRule.GilSacks, RewardRule.GilSacks]);
        Assert.Equal(5, order.Count);
        Assert.Equal(RewardRule.GilSacks, order[0]);
        Assert.Equal(RewardPicker.DefaultOrder.Where(r => r != RewardRule.GilSacks), order.Skip(1));
        Assert.Equal(RewardPicker.DefaultOrder, RewardPicker.Normalise([]));
    }
}
