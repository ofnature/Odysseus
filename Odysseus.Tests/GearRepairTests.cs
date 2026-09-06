using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class GearRepairTests
{
    private static void Run(GearRepair repair, FakeStepWorld world, int ticks = 40)
    {
        for (var i = 0; i < ticks && repair.Busy; i++) { repair.Tick(); world.Advance(0.5); }
    }

    [Fact]
    public void Worn_gear_is_repaired_through_the_window_and_its_confirmation()
    {
        var world = new FakeStepWorld { LowestGearConditionPercent = 12 };
        var repair = new GearRepair(world, _ => { });
        Assert.True(repair.Needed(30));
        Assert.False(repair.Needed(10));
        Assert.False(repair.Needed(0));   // zero means never

        repair.Begin();
        Run(repair, world);

        Assert.Equal(RepairState.Done, repair.State);
        Assert.Equal(["OpenRepair", "RepairAll", "YesNo True", "CloseRepair"],
            world.Calls.Where(c => c is "OpenRepair" or "RepairAll" or "YesNo True" or "CloseRepair"));
        Assert.Equal(100, world.LowestGearConditionPercent);
    }

    [Fact]
    public void A_window_that_never_opens_is_an_honest_fault()
    {
        var world = new FakeStepWorld { LowestGearConditionPercent = 12, RepairWindowOpens = false };
        var repair = new GearRepair(world, _ => { });
        repair.Begin();
        Run(repair, world);

        Assert.Equal(RepairState.Faulted, repair.State);
        Assert.Contains("did not open", repair.FailReason);
    }

    [Fact]
    public void Gear_that_does_not_mend_names_dark_matter_and_levels()
    {
        // The window opens and the button presses, but nothing changes — no dark matter.
        var world = new FakeStepWorld { LowestGearConditionPercent = 12, RepairAllMends = false };
        var repair = new GearRepair(world, _ => { });
        repair.Begin();
        Run(repair, world);

        Assert.Equal(RepairState.Faulted, repair.State);
        Assert.Contains("dark matter", repair.FailReason);
        Assert.Contains("CloseRepair", world.Calls);   // never left open
    }
}
