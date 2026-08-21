using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class GroundedPathTests
{
    [Fact]
    public void The_bundles_allied_society_folders_are_recognised()
    {
        Assert.True(new QuestPath { Category = "2.x - A Realm Reborn/Allied Societies/Amalj'aa/Story" }.IsAlliedSociety);
        Assert.True(new QuestPath { Category = "2.x - A Realm Reborn/Allied Society Quests (A Realm Reborn through Endwalker)" }.IsAlliedSociety);
        Assert.True(new QuestPath { Category = "7.x - Dawntrail/Allied Societies/Pelupelu" }.IsAlliedSociety);

        Assert.False(new QuestPath { Category = "2.x - A Realm Reborn/MSQ/A-1" }.IsAlliedSociety);
        Assert.False(new QuestPath { Category = "5.x - Shadowbringers/Custom Deliveries/Kai-Shirr" }.IsAlliedSociety);
        Assert.False(new QuestPath().IsAlliedSociety);
    }

    [Fact]
    public void A_grounded_step_walks_a_route_the_data_asked_to_fly()
    {
        Assert.Contains("fly=True", MoveCallsFor(groundOnly: false));
        Assert.DoesNotContain("fly=True", MoveCallsFor(groundOnly: true));
    }

    private static string MoveCallsFor(bool groundOnly)
    {
        var step = new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 146,
            Position = new Vector3(50, 0, 50), Fly = true, Mount = false,
        };
        var world = new FakeStepWorld { CanFlyHere = true, ArriveOnMove = false, TerritoryId = 146 };
        var ex = new StepExecutor(world);
        ex.Begin(step, groundOnly: groundOnly);
        for (var i = 0; i < 8; i++) { ex.Tick(); world.Advance(0.5); }

        var moves = world.Calls.Where(c => c.StartsWith("Move")).ToList();
        Assert.NotEmpty(moves);
        return string.Join(" | ", moves);
    }

    [Fact]
    public void A_grounded_path_drops_the_waypoints_that_were_only_there_for_the_flight()
    {
        // Peace for Thanalan's own shape: a waypoint up at y=43 that the author marked
        // "skip if flying is locked", with the ground route written underneath it.
        var midAir = new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", TerritoryId = 146,
            Position = new Vector3(-151, 43, -345), Fly = true,
            SkipConditions = new SkipConditions { StepIf = new StepCondition { Flying = "Locked" } },
        };
        var world = new FakeStepWorld { CanFlyHere = true, InBaseGameZone = true };
        var snap = new QuestSnapshot(1217, 1, new byte[6]);

        // Flying: the waypoint is part of the route.
        Assert.False(StepConditions.ShouldSkipStep(midAir, world, snap));

        // Grounded: it drops out, rather than being walked to in mid-air.
        Assert.True(StepConditions.ShouldSkipStep(midAir, new GroundedWorld(world), snap));
    }
}
