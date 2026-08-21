using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class StepExecutorTests
{
    private static QuestStep Step(StepKind kind, Vector3? pos = null, uint? dataId = null) => new()
    {
        Kind = kind, KindName = kind.ToString(), Position = pos, DataId = dataId, TerritoryId = 400,
    };

    /// <summary>Tick until the status leaves Running, advancing the clock, or give up.</summary>
    private static StepStatus Run(StepExecutor ex, FakeStepWorld world, int maxTicks = 200, double secondsPerTick = 0.5)
    {
        for (var i = 0; i < maxTicks; i++)
        {
            var s = ex.Tick();
            if (s != StepStatus.Running) return s;
            world.Advance(secondsPerTick);
        }
        return ex.Status;
    }

    /// <summary>
    /// DisableNavmesh marks the places the path author found the mesh gets wrong, so the step must
    /// walk straight there — and must not be gated on, or judged by, a pathfind that never runs.
    /// </summary>
    [Fact]
    public void DisableNavmesh_walks_straight_and_ignores_the_mesh()
    {
        var world = new FakeStepWorld { ArriveOnMove = true, NavmeshReady = false, PathWaypointCount = 0 };
        var ex = new StepExecutor(world);
        var step = Step(StepKind.WalkTo, new Vector3(10, 0, 10));
        step.DisableNavmesh = true;
        ex.Begin(step);

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains(world.Calls, c => c.StartsWith("MoveDirect 10,0,10"));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Move 10,0,10"));
    }

    [Fact]
    public void An_ordinary_step_still_waits_for_the_mesh()
    {
        var world = new FakeStepWorld { ArriveOnMove = true, NavmeshReady = false };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("navmesh not ready", ex.FailReason);
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Move"));
    }

    /// <summary>
    /// A mesh part-built is on its way. The eight-second stall clock is for one that is not coming;
    /// a fresh zone takes far longer than that, and failing on it errored out a run that only had
    /// to wait.
    /// </summary>
    [Fact]
    public void A_navmesh_still_building_is_waited_for_not_faulted()
    {
        var world = new FakeStepWorld { NavmeshReady = false, NavmeshBuildProgress = 0.4f };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));

        // Well past the stall clock, and still waiting rather than failed.
        for (var i = 0; i < 60; i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Equal(StepStatus.Running, ex.Status);

        world.NavmeshBuildProgress = -1f;
        world.NavmeshReady = true;
        world.ArriveOnMove = true;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    /// <summary>A mesh that is not building and not ready is still a fault, on the same clock.</summary>
    [Fact]
    public void A_navmesh_that_is_not_coming_still_faults()
    {
        var world = new FakeStepWorld { NavmeshReady = false, NavmeshBuildProgress = -1f };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("navmesh not ready", ex.FailReason);
    }

    /// <summary>
    /// The cities forbid mounts. Asking anyway puts an error on screen and then waits out the mount
    /// timer for something that is never coming.
    /// </summary>
    [Fact]
    public void A_zone_that_forbids_mounts_is_walked()
    {
        var world = new FakeStepWorld { CanMountHere = false, ArriveOnMove = true };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(500, 0, 0)));   // far enough to want one

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain("Mount", world.Calls);
    }

    [Fact]
    public void A_long_walk_outside_a_city_still_mounts()
    {
        var world = new FakeStepWorld { CanMountHere = true, ArriveOnMove = true };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(500, 0, 0)));
        Run(ex, world);

        Assert.Contains("Mount", world.Calls);
    }

    /// <summary>
    /// The seconds after a teleport are a lock, and a mount asked for inside it is dropped without
    /// a word — so it is asked again once the character can act rather than once on the way past.
    /// </summary>
    [Fact]
    public void A_mount_asked_for_during_the_post_teleport_lock_is_asked_again()
    {
        var world = new FakeStepWorld { CanMountHere = true, ArriveOnMove = true, IsReady = false };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(500, 0, 0)));

        for (var i = 0; i < 6; i++) { ex.Tick(); world.Advance(0.5); }
        Assert.DoesNotContain("Mount", world.Calls);   // nothing asked while locked

        world.IsReady = true;
        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Mount", world.Calls);
    }

    /// <summary>
    /// A cutscene owns the character and the mesh is not dependable through one. A step failed with
    /// "navmesh not ready" for no reason but that, so none of the clocks run while occupied.
    /// </summary>
    [Fact]
    public void A_cutscene_does_not_run_the_movement_clocks_down()
    {
        var world = new FakeStepWorld { IsOccupied = true, NavmeshReady = false, NavmeshBuildProgress = -1f };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));

        // Far past both the navmesh stall clock and the whole-move timeout.
        for (var i = 0; i < 500; i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Equal(StepStatus.Running, ex.Status);

        world.IsOccupied = false;
        world.NavmeshReady = true;
        world.ArriveOnMove = true;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void WalkTo_moves_and_is_done_on_arrival()
    {
        var world = new FakeStepWorld { ArriveOnMove = true };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains(world.Calls, c => c.StartsWith("Move 10,0,10"));
    }

    [Fact]
    public void Already_there_means_no_move_at_all()
    {
        var world = new FakeStepWorld { PlayerPosition = new Vector3(10, 0, 10) };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));

        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.DoesNotContain(world.Calls, c => c.StartsWith("Move"));
    }

    [Fact]
    public void Long_legs_mount_first_short_ones_do_not()
    {
        var far = new FakeStepWorld { ArriveOnMove = true };
        var ex = new StepExecutor(far);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(100, 0, 0)));
        Run(ex, far);
        Assert.Contains("Mount", far.Calls);

        var near = new FakeStepWorld { ArriveOnMove = true };
        ex = new StepExecutor(near);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 0)));
        Run(ex, near);
        Assert.DoesNotContain("Mount", near.Calls);
    }

    [Fact]
    public void Mount_false_on_the_step_forbids_mounting()
    {
        var world = new FakeStepWorld { ArriveOnMove = true };
        var ex = new StepExecutor(world);
        var step = Step(StepKind.WalkTo, new Vector3(100, 0, 0));
        step.Mount = false;
        ex.Begin(step);
        Run(ex, world);
        Assert.DoesNotContain("Mount", world.Calls);
    }

    [Fact]
    public void Fly_is_only_requested_where_flying_is_unlocked()
    {
        var grounded = new FakeStepWorld { ArriveOnMove = true, CanFlyHere = false };
        var ex = new StepExecutor(grounded);
        var step = Step(StepKind.WalkTo, new Vector3(10, 0, 0));
        step.Fly = true;
        ex.Begin(step);
        Run(ex, grounded);
        Assert.Contains(grounded.Calls, c => c.StartsWith("Move") && c.EndsWith("fly=False"));

        var flying = new FakeStepWorld { ArriveOnMove = true, CanFlyHere = true };
        ex = new StepExecutor(flying);
        ex.Begin(step);
        Run(ex, flying);
        Assert.Contains(flying.Calls, c => c.StartsWith("Move") && c.EndsWith("fly=True"));
    }

    [Fact]
    public void Unreachable_destination_fails_with_a_reason_instead_of_spinning()
    {
        var world = new FakeStepWorld { PathWaypointCount = 0 };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));

        // Zero waypoints is asked again before it is believed — a mesh still loading answers that
        // way for a moment — so the step ends after the retries, not on the first answer.
        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no path", ex.FailReason);
        Assert.Equal(3, world.Calls.Count(c => c.StartsWith("Move ")));
    }

    [Fact]
    public void Interact_walks_up_interacts_waits_out_the_dialogue_and_is_done()
    {
        var world = new FakeStepWorld { ArriveOnMove = true };
        world.Spawned.Add(1012081);
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.Interact, new Vector3(20, 0, 0), 1012081));

        // Tick until the interact fires, then simulate a dialogue opening and closing.
        Run(ex, world, maxTicks: 6);
        Assert.Contains("Interact 1012081", world.Calls);
        Assert.Contains("HoldDialogue", world.Calls);
        Assert.Equal(StepStatus.Running, ex.Status);

        world.IsOccupied = true;
        ex.Tick(); world.Advance(1);
        world.IsOccupied = false;
        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("ReleaseDialogue", world.Calls);
    }

    [Fact]
    public void Interact_without_a_dialogue_still_finishes_after_a_settle_period()
    {
        var world = new FakeStepWorld { PlayerPosition = new Vector3(20, 0, 0) };
        world.Spawned.Add(7);
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.Interact, new Vector3(20, 0, 0), 7));

        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void Interact_answers_a_yes_no_prompt_the_step_carries()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.Spawned.Add(7);
        var ex = new StepExecutor(world);
        var step = Step(StepKind.Interact, null, 7);
        step.DialogueChoices = [new DialogueChoice("YesNo", "TEXT_X", null, true)];
        ex.Begin(step);
        Run(ex, world, maxTicks: 3);

        world.IsOccupied = true;
        world.VisibleAddons.Add("SelectYesno");
        ex.Tick();
        Assert.Contains("YesNo True", world.Calls);
    }

    [Fact]
    public void Missing_object_fails_after_the_wait_not_forever()
    {
        var world = new FakeStepWorld();
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.Interact, null, 99));

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("99 never appeared", ex.FailReason);
    }

    [Fact]
    public void Combat_on_enter_area_waits_for_the_fight_and_is_done_when_it_is_quiet()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var ex = new StepExecutor(world);
        var step = Step(StepKind.Combat, Vector3.Zero);
        step.EnemySpawnType = EnemySpawnType.AutoOnEnterArea;
        step.KillEnemyDataIds = [4015];
        ex.Begin(step);

        Run(ex, world, maxTicks: 3);
        Assert.Equal(StepStatus.Running, ex.Status);

        world.InCombat = true;
        for (var i = 0; i < 10; i++) { ex.Tick(); world.Advance(1); }
        Assert.Equal(StepStatus.Running, ex.Status);

        world.InCombat = false;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void Combat_pulls_a_remaining_enemy_before_calling_it_clear()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        world.AttackResults.Enqueue(true);
        var ex = new StepExecutor(world);
        var step = Step(StepKind.Combat, Vector3.Zero);
        step.EnemySpawnType = EnemySpawnType.OverworldEnemies;
        ex.Begin(step);

        Run(ex, world, maxTicks: 3);
        Assert.Contains("Attack", world.Calls);
    }

    [Fact]
    public void Combat_with_nothing_to_fight_gives_up_waiting_and_is_done()
    {
        var world = new FakeStepWorld { PlayerPosition = Vector3.Zero };
        var ex = new StepExecutor(world);
        var step = Step(StepKind.Combat, Vector3.Zero);
        step.EnemySpawnType = EnemySpawnType.AutoOnEnterArea;
        ex.Begin(step);
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void Unsupported_kinds_fail_immediately_and_say_which()
    {
        var world = new FakeStepWorld();
        var ex = new StepExecutor(world);
        var step = Step(StepKind.Unknown);
        step.KindName = "SomethingNew";
        ex.Begin(step);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("SomethingNew", ex.FailReason);
    }

    [Fact]
    public void Dying_fails_the_step()
    {
        var world = new FakeStepWorld { IsDead = true };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(5, 0, 5)));
        Assert.Equal(StepStatus.Failed, ex.Tick());
    }
}
public class UseItemRangeTests
{
    private static QuestStep Treat(uint target) => new()
    {
        Kind = StepKind.UseItem, KindName = "UseItem", DataId = target, ItemId = 2001288,
        TerritoryId = 137, Position = new System.Numerics.Vector3(519, 9, 246),
    };

    [Fact]
    public void An_item_used_on_someone_out_of_reach_walks_in_first()
    {
        // "They Came from the Deep": four survivors treated with event item 2001288. The steps ran
        // and nothing happened.
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = true };
        w.PlayerPosition = new System.Numerics.Vector3(519, 9, 246); // already where the step says
        w.Spawned.Add(1008830);
        w.Positions[1008830] = new System.Numerics.Vector3(519, 9, 260); // the survivor is fourteen yalms off
        var ex = new StepExecutor(w);
        ex.Begin(Treat(1008830));
        for (var i = 0; i < 10 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("walking into range first"));

        // The walk comes first: the item is not used from fourteen yalms away.
        var walked = w.Calls.FindIndex(c => c.StartsWith("Move"));
        var used = w.Calls.FindIndex(c => c == "UseItem 2001288");
        Assert.True(walked >= 0 && used > walked, string.Join(" | ", w.Calls));
    }

    [Fact]
    public void Standing_next_to_them_it_just_uses_the_item()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = true };
        w.PlayerPosition = new System.Numerics.Vector3(519, 9, 246);
        w.Spawned.Add(1008830); // unplaced, so it is right there
        var ex = new StepExecutor(w);
        ex.Begin(Treat(1008830));
        for (var i = 0; i < 10 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Contains(w.Calls, c => c == "UseItem 2001288");
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Log") && c.Contains("walking into range"));
    }
}
public class ItemRefusalTests
{
    private static QuestStep Treat(uint target) => new()
    {
        Kind = StepKind.UseItem, KindName = "UseItem", DataId = target, ItemId = 2001288,
        TerritoryId = 137, Position = new System.Numerics.Vector3(519, 9, 246),
    };

    private static FakeStepWorld AtTheSurvivor()
    {
        var w = new FakeStepWorld { TerritoryId = 137, ArriveOnMove = true };
        w.PlayerPosition = new System.Numerics.Vector3(519, 9, 246);
        w.Spawned.Add(1008830);
        return w;
    }

    [Fact]
    public void The_target_is_faced_before_the_item_is_used_on_them()
    {
        // The walk ends pointed along its last leg, which after the straight-line finish is usually
        // past the survivor rather than at them — and an item used on someone you are looking away
        // from does nothing.
        var w = AtTheSurvivor();
        var ex = new StepExecutor(w);
        ex.Begin(Treat(1008830));
        for (var i = 0; i < 10 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }

        var faced = w.Calls.FindIndex(c => c == "Face 1008830");
        var used = w.Calls.FindIndex(c => c == "UseItem 2001288");
        Assert.True(faced >= 0 && used > faced, string.Join(" | ", w.Calls));
    }

    [Fact]
    public void A_refused_item_is_tried_again_before_the_step_is_given_up_on()
    {
        var w = AtTheSurvivor();
        w.UseItemAccepted = false;
        var ex = new StepExecutor(w);
        ex.Begin(Treat(1008830));
        for (var i = 0; i < 60 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }

        Assert.True(w.Calls.Count(c => c == "UseItem 2001288") > 1, "gave up on the first refusal");
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("would not let us use item", ex.FailReason);
    }

    [Fact]
    public void A_refusal_that_passes_lets_the_step_through()
    {
        var w = AtTheSurvivor();
        w.UseItemAccepted = false;
        var ex = new StepExecutor(w);
        ex.Begin(Treat(1008830));
        for (var i = 0; i < 6; i++) { ex.Tick(); w.Advance(0.5); }

        w.UseItemAccepted = true; // whatever was in the way has passed
        for (var i = 0; i < 20 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void You_cannot_use_a_quest_item_from_the_saddle()
    {
        var w = AtTheSurvivor();
        w.IsMounted = true;
        w.HoldsMount = true; // still coming down
        var ex = new StepExecutor(w);
        ex.Begin(Treat(1008830));
        for (var i = 0; i < 10; i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Contains("Dismount", w.Calls);
        Assert.DoesNotContain(w.Calls, c => c == "UseItem 2001288");

        w.HoldsMount = false;
        w.IsMounted = false;
        for (var i = 0; i < 20 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains(w.Calls, c => c == "UseItem 2001288");
        Assert.Equal(StepStatus.Done, ex.Status);
    }
}
