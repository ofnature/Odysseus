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
        // way for a moment. When the mesh claims both ends and still gives nothing, it is rebuilt
        // once and the attempts start over; only then is the failure believed.
        Assert.Equal(StepStatus.Failed, Run(ex, world));
        Assert.Contains("no path", ex.FailReason);
        Assert.Equal(1, world.Calls.Count(c => c == "RebuildNavmesh"));
        Assert.Equal(6, world.Calls.Count(c => c.StartsWith("Move ")));
    }

    [Fact]
    public void A_hand_over_window_is_joined_from_any_phase_of_any_step()
    {
        // Kurobana's vinegar: the Request window opened while the step was mid-move, and the
        // fill-and-confirm lives in the dialogue machinery. A Request can only belong to the
        // quest being run — join it whatever the step kind.
        var w = new FakeStepWorld { TerritoryId = 614, TalksWhenInteracted = false };
        w.PlayerPosition = new Vector3(600, 68, -137);
        w.Spawned.Add(1022414);
        w.IsOccupied = true;
        w.VisibleAddons.Add("Request");
        w.Requests.Add(new HandOverRequest(2003718, "Vial of Wood Vinegar", 1));
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 1022414, TerritoryId = 614, Position = new Vector3(605, 68, -137) });
        for (var i = 0; i < 30 && !w.Calls.Any(c => c.StartsWith("HandOver")); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains(w.Calls, c => c.StartsWith("HandOver"));
    }

    [Fact]
    public void A_turn_in_whose_chain_opened_mid_travel_joins_the_choice_instead_of_freezing()
    {
        // Clutch and Kin: the join choice opened straight off the last objective, the
        // CompleteQuest step was still in its move phase far from the issuer, and both waited
        // on each other for seven minutes.
        var w = new FakeStepWorld { TerritoryId = 138, TalksWhenInteracted = false };
        w.PlayerPosition = new Vector3(-38, -23, -89);           // still at the camp
        w.Spawned.Add(1005937);
        w.IsOccupied = true;
        w.VisibleAddons.Add("SelectString");
        w.ListEntries.AddRange(["I swear myself to your noble cause. Pshhh.", "I don't believe I can trust you."]);
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.CompleteQuest, KindName = "CompleteQuest", DataId = 1005937, TerritoryId = 138, Position = new Vector3(-238, -41, 68) });

        for (var i = 0; i < 40 && !w.Calls.Contains("Select 0"); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains("Select 0", w.Calls);   // the first answer, after the undeclared-list grace
    }

    [Fact]
    public void A_turn_in_joins_a_conversation_the_previous_hand_in_left_open()
    {
        // Third Kobold hand-in: the second quest's chain was still up (ending at the overcap
        // warning), the interact phase waited behind it for thirty seconds, and the daily was
        // dropped one Yes away from done. An open conversation IS the turn-in — join it.
        var w = new FakeStepWorld { ArriveOnMove = true, TerritoryId = 180 };
        w.PlayerPosition = new Vector3(7, 16, -189);
        w.Spawned.Add(1005928);
        w.IsOccupied = true;                       // the previous hand-in's chain, still open
        w.VisibleAddons.Add("SelectYesno");
        w.OvercapDialogUp = true;
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.CompleteQuest, KindName = "CompleteQuest", DataId = 1005928, TerritoryId = 180, Position = new Vector3(7, 16, -189) });
        for (var i = 0; i < 20 && !w.Calls.Contains("OvercapYes"); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains("OvercapYes", w.Calls);    // answered from the joined dialogue, not timed out
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Interact "));  // no press into an open window
    }

    [Fact]
    public void An_accept_step_presses_the_offer_windows_own_accept()
    {
        // Kurobana vs. the Arrowheads sat at its offer for ninety seconds: TextAdvance's accept
        // toggle was off, and only the society loop knew how to press the button.
        var w = new FakeStepWorld { ArriveOnMove = true, TalkLength = TimeSpan.FromSeconds(60), TerritoryId = 614 };
        w.PlayerPosition = new Vector3(474, 58, -183);
        w.Spawned.Add(1019312);
        w.VisibleAddons.Add("JournalAccept");
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.AcceptQuest, KindName = "AcceptQuest", DataId = 1019312, TerritoryId = 614, Position = new Vector3(474, 58, -183) });
        for (var i = 0; i < 30 && !w.Calls.Contains("AcceptQuestOffer"); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains("AcceptQuestOffer", w.Calls);
    }

    [Fact]
    public void The_reward_overcap_warning_is_answered_yes_unless_the_toggle_says_wait()
    {
        // Capped tomestones at a turn-in: "you will not be able to receive all the following" sat
        // unanswered until the dialogue clock faulted the hand-in.
        var w = new FakeStepWorld { ArriveOnMove = true, TalkLength = TimeSpan.FromSeconds(60) };
        w.Spawned.Add(1005550);
        w.VisibleAddons.Add("SelectYesno");
        w.OvercapDialogUp = true;
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep { Kind = StepKind.CompleteQuest, KindName = "CompleteQuest", DataId = 1005550, TerritoryId = 400, Position = new Vector3(0, 0, 0) });
        for (var i = 0; i < 30 && !w.Calls.Contains("OvercapYes"); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Contains("OvercapYes", w.Calls);

        // Toggled off: the dialog is the player's to answer.
        var held = new FakeStepWorld { ArriveOnMove = true, TalkLength = TimeSpan.FromSeconds(60) };
        held.Spawned.Add(1005550);
        held.VisibleAddons.Add("SelectYesno");
        held.OvercapDialogUp = true;
        ex = new StepExecutor(held, acceptOvercap: () => false);
        ex.Begin(new QuestStep { Kind = StepKind.CompleteQuest, KindName = "CompleteQuest", DataId = 1005550, TerritoryId = 400, Position = new Vector3(0, 0, 0) });
        for (var i = 0; i < 20; i++) { ex.Tick(); held.Advance(0.5); }
        Assert.DoesNotContain("OvercapYes", held.Calls);
    }

    [Fact]
    public void Within_reach_of_the_object_is_arrived_wherever_the_mark_sits()
    {
        // The Zanr'ak succulent: the recorded mark is inside the plant's own collision, the world
        // refuses the last yalm, and the walk laddered every remedy at 2.9y — within interact
        // reach the whole time — while the interact never got its turn.
        var mark = new Vector3(-29, 7, -88);
        var world = new FakeStepWorld { PathWaypointCount = 0, TerritoryId = 146 };
        world.PlayerPosition = new Vector3(-31.5f, 7, -87);           // 2.6y from the mark
        world.Spawned.Add(2002981);
        world.Positions[2002981] = new Vector3(-29.5f, 7, -87.5f);   // the plant, well in reach
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Interact, KindName = "Interact", DataId = 2002981,
            TerritoryId = 146, Position = mark,
        });
        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains("Interact 2002981", world.Calls);
    }

    [Fact]
    public void An_interact_the_ground_cannot_serve_flies_to_the_object_itself()
    {
        // Kurobana on his knoll: 4.2y away through a lip, every press eaten, every walk short.
        // The universal escape — fly to the thing, land on its floor, press from there.
        var world = new FakeStepWorld { TerritoryId = 614, CanFlyHere = true, TalksWhenInteracted = false, ArriveOnMove = true };
        world.PlayerPosition = new Vector3(366, 97, -94);
        world.Spawned.Add(1022417);
        world.Positions[1022417] = new Vector3(366, 101, -94);
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 1022417, TerritoryId = 614, Position = new Vector3(366, 101, -94) });

        for (var i = 0; i < 120 && !world.Calls.Any(c => c.StartsWith("Log") && c.Contains("flying to it")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("Log") && c.Contains("flying to it"));

        // Put the character back below the knoll so the flight has somewhere to go.
        world.PlayerPosition = new Vector3(366, 97, -94);
        for (var i = 0; i < 20 && !world.Calls.Any(c => c.StartsWith("Move 366,101,-94 fly=True")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("Move 366,101,-94 fly=True"));
    }

    [Fact]
    public void Standing_under_the_objects_ledge_is_not_arrival()
    {
        // Kurobana stands on a ledge at Y=86; the ride ended a floor below him and the symmetric
        // vertical tolerance called it arrived. Below the object's floor, keep travelling.
        var world = new FakeStepWorld { TerritoryId = 614, PathWaypointCount = 5, ArriveOnMove = true };
        world.PlayerPosition = new Vector3(635, 77, -147);   // nine yalms under the ledge
        world.Spawned.Add(1022421);
        world.Positions[1022421] = new Vector3(635, 86, -147);
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Interact, KindName = "Interact", DataId = 1022421, TerritoryId = 614, Position = new Vector3(635, 86, -147) });
        for (var i = 0; i < 20 && !world.Calls.Any(c => c.StartsWith("Move ")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("Move 635,86,-147"));   // still riding to the mark up top
    }

    [Fact]
    public void An_interact_reached_mounted_lands_and_dismounts_before_it_acts()
    {
        // "Pin to the floor": the flight ends hovering eight yalms over the succulent. That is
        // arrival — and the step gets off the mount (the game's own descent) before touching it.
        var world = new FakeStepWorld { PathWaypointCount = 0, TerritoryId = 146, IsMounted = true };
        world.PlayerPosition = new Vector3(-29, 15, -88);             // right above it, in the air
        world.Spawned.Add(2002981);
        world.Positions[2002981] = new Vector3(-29, 7, -88);
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Interact, KindName = "Interact", DataId = 2002981, Fly = true,
            TerritoryId = 146, Position = new Vector3(-29, 7, -88),
        });
        for (var i = 0; i < 30 && !world.Calls.Contains("Interact 2002981"); i++)
        {
            ex.Tick();
            if (!world.IsMounted) world.PlayerPosition = world.Positions[2002981]; // landed
            world.Advance(0.5);
        }
        var dismount = world.Calls.IndexOf("Dismount");
        var interact = world.Calls.IndexOf("Interact 2002981");
        Assert.True(dismount >= 0, "never dismounted");
        Assert.True(interact >= 0, "never interacted");
        Assert.True(dismount < interact, "interacted from the saddle");
    }

    [Fact]
    public void A_leg_the_ground_cannot_route_flies_when_the_path_says_to()
    {
        // Zanr'ak's succulents: fenced camp, Fly true in the data, ground-only for tribe runs —
        // and five mesh rebuilds proved the ground truly has no way in. The preference yields.
        var world = new FakeStepWorld { PathWaypointCount = 0, CanFlyHere = true, TerritoryId = 146 };
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.WalkTo, KindName = "WalkTo", Fly = true,
            TerritoryId = 146, Position = new Vector3(-116, -1, -33),
        }, groundOnly: true);

        for (var i = 0; i < 20 && !world.Calls.Any(c => c.Contains("fly=True")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("Move ") && c.Contains("fly=True"));

        world.PathWaypointCount = 5; world.ArriveOnMove = true;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void A_mesh_that_lies_about_the_world_is_rebuilt_and_the_step_then_succeeds()
    {
        // Korha after unlocking the Amalj'aa: the zone changed shape, the cached mesh predates it,
        // and three different marks "had no path" while sitting on walkable ground.
        var world = new FakeStepWorld { PathWaypointCount = 0 };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(50, 0, 50)));
        for (var i = 0; i < 20 && !world.Calls.Contains("RebuildNavmesh"); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains("RebuildNavmesh", world.Calls);

        // The fresh mesh knows the way.
        world.PathWaypointCount = 5; world.ArriveOnMove = true;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void No_path_says_whether_it_is_the_mesh_under_your_feet_or_the_destination()
    {
        // Korha, Peace for Thanalan: every walk in the zone "no path" — vnavmesh was ready and
        // holding a mesh that did not cover where she stood. The message should say so, because
        // the fix (/vnav rebuild) is nothing like the fix for a destination behind a door.
        var stale = new FakeStepWorld { PathWaypointCount = 0, PlayerPosition = new Vector3(1, 0, 1) };
        stale.NearestReachableFn = (p, _) => null;   // nothing reachable, not even our own feet
        var ex = new StepExecutor(stale);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));
        Assert.Equal(StepStatus.Failed, Run(ex, stale));
        Assert.Contains("does not cover where you stand", ex.FailReason);
        Assert.Contains("/vnav rebuild", ex.FailReason);

        var door = new FakeStepWorld { PathWaypointCount = 0, PlayerPosition = new Vector3(1, 0, 1) };
        door.NearestReachableFn = (p, _) => p == door.PlayerPosition ? p : null;   // our feet are fine; the target is not
        ex = new StepExecutor(door);
        ex.Begin(Step(StepKind.WalkTo, new Vector3(10, 0, 10)));
        Assert.Equal(StepStatus.Failed, Run(ex, door));
        Assert.Contains("no route from here to there", ex.FailReason);
    }

    [Fact]
    public void A_destination_off_the_mesh_is_reached_by_its_nearest_mesh_point_and_a_walk()
    {
        // Hamujj Gah's platform is painted non-walkable: the WalkTo beside him has no mesh under it.
        // The mesh is asked where it can get to, that is pathed to, and the last few yalms are on foot.
        var target = new Vector3(107, 15, -361);
        var edge = new Vector3(103, 15, -358);
        var world = new FakeStepWorld { PathWaypointCount = 0, PlayerPosition = new Vector3(90, 15, -350) };
        world.NearestReachableFn = (p, _) => p == world.PlayerPosition ? p : Vector3.Distance(p, target) < 1 ? edge : null;
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, target));

        // First ask: nothing. Second look: snap to the edge and go there.
        for (var i = 0; i < 12 && !world.Calls.Any(c => c.StartsWith("Move 103,15,-358")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("Move 103,15,-358"));
        world.PlayerPosition = edge;   // arrived at the mesh's edge, standing still

        for (var i = 0; i < 12 && !world.Calls.Any(c => c.StartsWith("MoveDirect 107,15,-361")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("MoveDirect 107,15,-361"));
        world.PlayerPosition = target;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void Off_mesh_feet_step_onto_the_mesh_before_the_next_path_is_asked_for()
    {
        // Having walked onto the platform for the last step, the next step's pathfind — to the
        // other side of the zone — cannot start. The mesh's edge is two yalms away; step there first.
        var feet = new Vector3(107, 15, -361);
        var edge = new Vector3(105, 15, -359);
        var far = new Vector3(-269, 5, -77);
        var world = new FakeStepWorld { PathWaypointCount = 0, PlayerPosition = feet };
        world.NearestReachableFn = (p, _) => Vector3.Distance(p, feet) < 1 ? edge : p;
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, far));
        for (var i = 0; i < 12 && !world.Calls.Any(c => c.StartsWith("MoveDirect 105,15,-359")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("MoveDirect 105,15,-359"));

        // On the mesh now: the pathfinder works, and the walk goes through.
        world.PlayerPosition = edge; world.PathWaypointCount = 5; world.ArriveOnMove = true;
        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.Contains(world.Calls, c => c.StartsWith("Move -269,5,-77"));
    }

    [Fact]
    public void A_walkto_the_world_stops_three_yalms_short_of_is_a_waypoint_reached()
    {
        // Brotherhood of Ash: the mark is three yalms inside something solid. Pathfinder silent,
        // direct walk stalled, three tries gone. The mark's job was to get us here; it has.
        var target = new Vector3(107, 15, -361);
        var world = new FakeStepWorld { PathWaypointCount = 0, PlayerPosition = new Vector3(104, 15, -361) };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, target));
        Assert.Equal(StepStatus.Done, Run(ex, world));

        // Six yalms is not "here"; that is still a failure.
        world = new FakeStepWorld { PathWaypointCount = 0, PlayerPosition = new Vector3(101, 15, -361) };
        world.NearestReachableFn = (p, _) => p;   // everything on the mesh, nothing to snap to, just no path
        ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, target));
        Assert.Equal(StepStatus.Failed, Run(ex, world));
    }

    [Fact]
    public void Standing_on_the_platform_a_few_yalms_short_it_just_walks_there()
    {
        // Off-mesh feet: the pathfind cannot start, and the nearest-point query finds the edge a
        // yalm away so it does not say so. Close enough to walk blind — so walk.
        var target = new Vector3(107, 15, -361);
        var world = new FakeStepWorld { PathWaypointCount = 0, PlayerPosition = new Vector3(101, 15, -358) };
        var ex = new StepExecutor(world);
        ex.Begin(Step(StepKind.WalkTo, target));
        for (var i = 0; i < 12 && !world.Calls.Any(c => c.StartsWith("MoveDirect")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("MoveDirect 107,15,-361"));
        world.PlayerPosition = target;
        Assert.Equal(StepStatus.Done, Run(ex, world));
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

    [Fact]
    public void A_combat_step_baited_by_an_emote_dozes_at_the_mark_then_waits_for_the_fight()
    {
        // Yellow-jacket ambushes: doze at the bed and kill what wakes you.
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", DataId = 2005940, Emote = "doze",
            EnemySpawnType = EnemySpawnType.AfterEmote, KillEnemyDataIds = [5042, 4619],
            TerritoryId = 152, Position = new System.Numerics.Vector3(245, -1, 101),
        };
        var w = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true };
        w.PlayerPosition = step.Position!.Value;
        w.Spawned.Add(2005940);

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 30 && !ex.PhaseName.Contains("Combat"); i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Contains(w.Calls, c => c == "Chat /doze");
        Assert.Equal("CombatWait", ex.PhaseName);
    }
    [Fact]
    public void A_combat_step_baited_by_a_cast_fires_it_then_waits_for_the_fight()
    {
        // The brazier that answers to Fire III, and what it spawns.
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", DataId = 2007872, ActionName = "Fire III",
            EnemySpawnType = EnemySpawnType.AfterAction, KillEnemyDataIds = [7232],
            TerritoryId = 152, Position = new System.Numerics.Vector3(245, -1, 101),
        };
        var w = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true };
        w.PlayerPosition = step.Position!.Value;
        w.Spawned.Add(2007872);
        w.Actions["Fire III"] = 153;

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 30 && !ex.PhaseName.Contains("Combat"); i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Contains(w.Calls, c => c.StartsWith("UseAction 153"));
        Assert.Equal("CombatWait", ex.PhaseName);
    }
    [Fact]
    public void Optional_combat_with_no_one_here_finishes_without_sitting_out_the_spawn_wait()
    {
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.FinishCombatIfAny,
            KillEnemyDataIds = [2870], TerritoryId = 152, Position = new System.Numerics.Vector3(245, -1, 101),
        };
        var w = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true };
        w.PlayerPosition = step.Position!.Value;

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 6 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }

        // Three seconds, not the fifteen the mandatory spawn wait takes.
        Assert.Equal(StepStatus.Done, ex.Status);
    }
    [Fact]
    public void Optional_combat_still_kills_the_leftovers_that_are_here()
    {
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.FinishCombatIfAny,
            KillEnemyDataIds = [2870], TerritoryId = 152, Position = new System.Numerics.Vector3(245, -1, 101),
        };
        var w = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true };
        w.PlayerPosition = step.Position!.Value;
        w.AttackResults.Enqueue(true);

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 10 && ex.PhaseName != "Combat"; i++) { ex.Tick(); w.Advance(0.5); }

        Assert.Equal("Combat", ex.PhaseName);
    }
    [Fact]
    public void A_step_marked_land_gets_off_the_mount_before_it_acts()
    {
        // Land: true — the flight ends in the air over the mark; the interact happens from the ground.
        var step = new QuestStep
        {
            Kind = StepKind.Interact, KindName = "Interact", DataId = 2002979, Land = true, Fly = true,
            TerritoryId = 152, Position = new System.Numerics.Vector3(245, -1, 101),
        };
        var w = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true, IsMounted = true };
        w.PlayerPosition = step.Position!.Value;
        w.Spawned.Add(2002979);
        w.Positions[2002979] = new System.Numerics.Vector3(246, -1, 100);

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 30 && ex.Status == StepStatus.Running && !w.Calls.Contains("Interact 2002979"); i++)
        { ex.Tick(); w.Advance(0.5); }

        var dismount = w.Calls.IndexOf("Dismount");
        var interact = w.Calls.IndexOf("Interact 2002979");
        Assert.True(dismount >= 0, "never dismounted");
        Assert.True(interact >= 0, "never interacted");
        Assert.True(dismount < interact, "interacted from the saddle");
    }
    [Fact]
    public void A_leg_that_gives_up_in_flight_lands_and_tries_again_on_foot()
    {
        // Level with the ring, 2.9y short, hover snagged: the give-up lands first, and only a
        // grounded failure settles for "near enough".
        var mark = new Vector3(-41, -23, -90);
        var world = new FakeStepWorld { TerritoryId = 138, IsMounted = true, IsInFlight = true, CanFlyHere = true, PathWaypointCount = 0 };
        world.PlayerPosition = new Vector3(-41, -23, -92.9f);
        world.NearestReachableFn = (p, _) => p;   // mesh claims everything, gives nothing
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", Fly = true, TerritoryId = 138, Position = mark });

        for (var i = 0; i < 40 && !world.Calls.Contains("Dismount"); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("Log") && c.Contains("landing to finish on foot"));
        Assert.Contains("Dismount", world.Calls);

        // On foot the ring is stepped into (or, here, the grounded retries settle for the mark).
        world.IsInFlight = false; world.IsMounted = false;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void A_dive_step_presses_the_descent_bind_until_the_water_accepts()
    {
        // Gyorin the Namazu: swim to the mark, then under. The press is the game's own bind.
        var world = new FakeStepWorld { TerritoryId = 614, IsSwimming = true };
        world.PlayerPosition = new Vector3(-488, -1, 579);
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.Dive, KindName = "Dive", TerritoryId = 614, Position = new Vector3(-488, -1, 579) });
        Assert.Equal(StepStatus.Done, Run(ex, world));
        Assert.True(world.Calls.Count(c => c == "Descend") >= 3);

        // Dry land is not a place to dive from.
        var dry = new FakeStepWorld { TerritoryId = 614 };
        dry.PlayerPosition = new Vector3(-488, -1, 579);
        ex = new StepExecutor(dry);
        ex.Begin(new QuestStep { Kind = StepKind.Dive, KindName = "Dive", TerritoryId = 614, Position = new Vector3(-488, -1, 579) });
        Assert.Equal(StepStatus.Failed, Run(ex, dry));
        Assert.Contains("not in the water", ex.FailReason);
    }

    [Fact]
    public void A_flight_hanging_over_a_walkto_mark_lands_before_judging_arrival()
    {
        // Clutch and Kin's destination ring: the flight ends hovering over it, the ring fires
        // only for someone standing in it, and a WalkTo has no object to pin the landing to.
        var mark = new Vector3(-41, -23, -90);
        var world = new FakeStepWorld { TerritoryId = 138, IsMounted = true, IsInFlight = true, CanFlyHere = true };
        world.PlayerPosition = new Vector3(-41, 2, -90);   // 25y straight up
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep { Kind = StepKind.WalkTo, KindName = "WalkTo", Fly = true, TerritoryId = 138, Position = mark });

        for (var i = 0; i < 20 && !world.Calls.Contains("Dismount"); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains("Dismount", world.Calls);
        Assert.Contains(world.Calls, c => c.StartsWith("Log") && c.Contains("landing"));

        // The descent puts us on the ground at the mark; the step then arrives for real.
        world.IsInFlight = false; world.IsMounted = false; world.PlayerPosition = mark;
        Assert.Equal(StepStatus.Done, Run(ex, world));
    }

    [Fact]
    public void A_flight_to_a_fight_lands_in_the_radius_and_walks_the_rest()
    {
        // Arrive flying near the combat mark: land there, then close the last stretch on foot.
        var mark = new Vector3(38, 3, -275);
        var world = new FakeStepWorld { TerritoryId = 146, IsMounted = true, CanFlyHere = true };
        world.PlayerPosition = new Vector3(48, 8, -275);   // 11y out, in the air
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.OverworldEnemies,
            KillEnemyDataIds = [742], Fly = true, TerritoryId = 146, Position = mark,
        });
        for (var i = 0; i < 20 && !world.Calls.Contains("Dismount"); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains("Dismount", world.Calls);
        Assert.Contains(world.Calls, c => c.StartsWith("Log") && c.Contains("landing to finish on foot"));

        // Feet down: the rest of the approach is walked, not flown.
        world.ArriveOnMove = true;
        for (var i = 0; i < 20 && !world.Calls.Any(c => c.StartsWith("Move 38,3,-275 fly=False")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("Move 38,3,-275 fly=False"));
    }

    [Fact]
    public void Named_overworld_targets_are_hunted_wide_and_unnamed_pulls_stay_close()
    {
        // The Banestools roam past the thirty-yalm ring — and Courage the Cowardly Lupin's
        // ambush stood off the mark the same way. With ids in hand the hunt goes as far as the
        // object table sees, whatever spawned them; the engage walk covers the distance.
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.OverworldEnemies,
            KillEnemyDataIds = [3437], TerritoryId = 152, Position = new Vector3(221, 5, 164),
        };
        var w = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true };
        w.PlayerPosition = step.Position!.Value;
        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 10 && !w.Calls.Contains("Attack"); i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal(StepExecutor.OverworldHuntRadius, w.LastAttackRadius);

        // No ids: wide would pull someone else's mobs. The tight ring stands.
        var any = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true };
        any.PlayerPosition = step.Position!.Value;
        ex = new StepExecutor(any);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.OverworldEnemies,
            TerritoryId = 152, Position = step.Position,
        });
        for (var i = 0; i < 10 && !any.Calls.Contains("Attack"); i++) { ex.Tick(); any.Advance(0.5); }
        Assert.Equal(StepExecutor.CombatSearchRadius, any.LastAttackRadius);
    }

    [Fact]
    public void An_arrival_spawn_that_stays_quiet_gets_the_last_steps_onto_the_mark()
    {
        // The Cowardly Lupin's ambush trigger sits where the author stood; arrival settles
        // 4.5y short. Quiet for a few seconds → stand exactly on the mark, then wait fresh.
        var mark = new Vector3(497, 55, 191);
        var world = new FakeStepWorld { TerritoryId = 614 };
        world.PlayerPosition = new Vector3(493, 55, 191);   // arrived, tolerance-short
        var ex = new StepExecutor(world);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.AutoOnEnterArea,
            KillEnemyDataIds = [7539], TerritoryId = 614, Position = mark,
        });
        for (var i = 0; i < 30 && !world.Calls.Any(c => c.StartsWith("MoveDirect 497,55,191")); i++) { ex.Tick(); world.Advance(0.5); }
        Assert.Contains(world.Calls, c => c.StartsWith("MoveDirect 497,55,191"));
        Assert.Contains(world.Calls, c => c.StartsWith("Log") && c.Contains("stepping exactly onto it"));
    }

    [Fact]
    public void Combat_gets_off_the_mount_before_it_pulls()
    {
        // Borderline Slaughter: ride to the spot, sit in the saddle, target mobs, swing nothing.
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.OverworldEnemies,
            KillEnemyDataIds = [742, 739], MinimumKillCount = 2,
            TerritoryId = 146, Position = new System.Numerics.Vector3(38, 3, -275),
        };
        var w = new FakeStepWorld { TerritoryId = 146, ArriveOnMove = true, IsMounted = true };
        w.PlayerPosition = step.Position!.Value;
        w.AttackResults.Enqueue(true);

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 20 && !w.Calls.Contains("Attack"); i++) { ex.Tick(); w.Advance(0.5); }

        var dismount = w.Calls.IndexOf("Dismount");
        var attack = w.Calls.IndexOf("Attack");
        Assert.True(dismount >= 0, "never dismounted");
        Assert.True(attack >= 0, "never pulled");
        Assert.True(dismount < attack, "pulled from the saddle");
    }

    [Fact]
    public void A_kill_count_is_paid_in_fights_before_the_step_calls_it_done()
    {
        // "Blitzing the Beacons" shape: overworld mobs, MinimumKillCount from ComplexCombatData.
        // Two wanted: one fight does not finish the step, the second does.
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", EnemySpawnType = EnemySpawnType.OverworldEnemies,
            KillEnemyDataIds = [2452], MinimumKillCount = 2,
            TerritoryId = 146, Position = new System.Numerics.Vector3(-9, 4, -52),
        };
        var w = new FakeStepWorld { TerritoryId = 146, ArriveOnMove = true };
        w.PlayerPosition = step.Position!.Value;
        w.AttackResults.Enqueue(true);

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 10 && ex.PhaseName != "Combat"; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal("Combat", ex.PhaseName);

        w.InCombat = true;  ex.Tick(); w.Advance(1);            // first fight
        Assert.Contains("Stop", w.Calls);                        // the approach stops when the fight starts
        w.InCombat = false;
        for (var i = 0; i < 12; i++) { ex.Tick(); w.Advance(0.5); }   // six seconds quiet, nothing to pull
        Assert.Equal(StepStatus.Running, ex.Status);           // one of two — still waiting for the respawn

        w.AttackResults.Enqueue(true); ex.Tick(); w.Advance(0.5);
        w.InCombat = true;  ex.Tick(); w.Advance(1);            // second fight
        w.InCombat = false;
        for (var i = 0; i < 12 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal(StepStatus.Done, ex.Status);
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
    public void The_items_cast_is_seen_out_before_the_step_lets_anything_move()
    {
        // The smothering ash: the use is a cast, and the next leg mounting through it cancels the
        // use silently — item spent, objective 0/5. The step must hold while the cast runs and
        // settle only after it ends.
        var w = AtTheSurvivor();
        var ex = new StepExecutor(w);
        ex.Begin(Treat(1008830));
        for (var i = 0; i < 6 && !w.Calls.Contains("UseItem 2001288"); i++) { ex.Tick(); w.Advance(0.5); }

        w.IsCasting = true;   // the cast bar is up
        for (var i = 0; i < 12; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal(StepStatus.Running, ex.Status);   // six seconds of casting, still held

        w.IsCasting = false;  // the cast lands
        ex.Tick(); w.Advance(0.5);
        Assert.Equal(StepStatus.Running, ex.Status);   // and the settle still runs after it

        for (var i = 0; i < 8 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void A_combat_step_that_spawns_from_a_thrown_item_throws_it_then_waits_for_the_fight()
    {
        // "Not Who They Seem": truesight scalebombs at suspicious objects, kill what appears.
        // Routing Combat straight to CombatWait sat next to the object doing nothing.
        var step = new QuestStep
        {
            Kind = StepKind.Combat, KindName = "Combat", DataId = 2003039, ItemId = 2001153,
            GroundTarget = true, EnemySpawnType = EnemySpawnType.AfterItemUse,
            KillEnemyDataIds = [72], TerritoryId = 152,
            Position = new System.Numerics.Vector3(245, -1, 101),
        };
        var w = new FakeStepWorld { TerritoryId = 152, ArriveOnMove = true };
        w.PlayerPosition = new System.Numerics.Vector3(245, -1, 101);
        w.Spawned.Add(2003039);
        w.Positions[2003039] = new System.Numerics.Vector3(246, -1, 100);

        var ex = new StepExecutor(w);
        ex.Begin(step);
        for (var i = 0; i < 20 && !ex.PhaseName.Contains("Combat"); i++) { ex.Tick(); w.Advance(0.5); }

        // Thrown at where the object stands, not used on a target — and then the fight is waited for.
        Assert.Contains(w.Calls, c => c.StartsWith("ThrowItem 2001153 @246"));
        Assert.DoesNotContain(w.Calls, c => c == "UseItem 2001153");
        Assert.Equal("CombatWait", ex.PhaseName);
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
