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
        // The move is accepted (IsMoving goes true while it pathfinds), then vnav reports it
        // found nothing: not moving, zero waypoints.
        Run(ex, world, maxTicks: 2);
        Assert.Contains(world.Calls, c => c.StartsWith("Move"));
        world.IsMoving = false;
        world.Advance(2);

        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no path", ex.FailReason);
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
