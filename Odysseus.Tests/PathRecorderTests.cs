using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;

namespace Odysseus.Tests;

public class PathRecorderTests
{
    private static readonly DateTime T0 = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A mutable observation builder so tests read like a play session.</summary>
    private sealed class Play
    {
        public DateTime Now = T0;
        public uint Territory = 400;
        public Vector3 Pos = new(10, 0, 10);
        public bool Occupied, InCombat, InDuty, TargetIsEnemy, Accepted, Complete, Teleported, DutySolo;
        public uint? Target, Cfc;
        public Vector3? TargetPos;
        public byte Seq;
        public byte[] Vars = new byte[6];
        public string? Arrival;

        public RecorderObservation Obs()
        {
            var quest = Accepted ? new QuestSnapshot(1622, Seq, Vars.ToArray()) : QuestSnapshot.Unavailable;
            var o = new RecorderObservation(Now, Territory, Pos, Occupied, InCombat, InDuty, Cfc, Target, TargetPos, TargetIsEnemy,
                quest, Accepted, Complete, Teleported, Arrival, DutySolo);
            Teleported = false; Arrival = null; // one-shot
            Now = Now.AddMilliseconds(500);
            return o;
        }
    }

    private static (PathRecorder rec, Play play) Start()
    {
        var rec = new PathRecorder();
        rec.Begin(1622, "Mogwin's Trial", "3.x/MSQ/recorded");
        var play = new Play();
        rec.Observe(play.Obs());
        return (rec, play);
    }

    private static void Talk(PathRecorder rec, Play play, uint npc, Vector3 at, Action? during = null)
    {
        play.Target = npc; play.TargetPos = at; play.TargetIsEnemy = false;
        rec.Observe(play.Obs());
        play.Occupied = true; rec.Observe(play.Obs());
        during?.Invoke();
        rec.Observe(play.Obs());
        play.Occupied = false; rec.Observe(play.Obs());
        play.Target = null; play.TargetPos = null;
    }

    [Fact]
    public void Accept_talk_and_hand_in_land_in_the_right_blocks_with_the_right_kinds()
    {
        var (rec, play) = Start();

        // Not accepted: talking to the giver is AcceptQuest in block 0; the game then says accepted, seq 1.
        Talk(rec, play, 1012083, new Vector3(355, -74, 639), during: () => { play.Accepted = true; play.Seq = 1; });
        // Seq 1: talk to an NPC; variables gain a bit while occupied.
        Talk(rec, play, 1012081, new Vector3(364, -73, 678), during: () => play.Vars = [16, 16, 0, 0, 0, 32]);
        // That talk advanced us to seq 2 too.
        play.Seq = 2; rec.Observe(play.Obs());
        // Hand in.
        play.Seq = 255; rec.Observe(play.Obs());
        Talk(rec, play, 1012083, new Vector3(355, -74, 639), during: () => play.Complete = true);

        var path = rec.Finish()!;
        Assert.Equal(1622, path.QuestId);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, path.Sequences.Select(s => s.Sequence).ToArray());

        var accept = Assert.Single(path.Block(0)!.Steps);
        Assert.Equal(StepKind.AcceptQuest, accept.Kind);
        Assert.Equal(1012083u, accept.DataId);

        var talk = Assert.Single(path.Block(1)!.Steps);
        Assert.Equal(StepKind.Interact, talk.Kind);
        Assert.Equal(1012081u, talk.DataId);
        Assert.Equal(new Vector3(364, -73, 678), talk.Position);
        Assert.Equal(new byte?[] { 16, 16, null, null, null, 32 }, talk.CompletionQuestVariablesFlags);

        var handIn = Assert.Single(path.Block(255)!.Steps);
        Assert.Equal(StepKind.CompleteQuest, handIn.Kind);
    }

    [Fact]
    public void A_teleport_arrival_becomes_the_shortcut_on_the_next_step()
    {
        var (rec, play) = Start();
        play.Accepted = true; play.Seq = 1; rec.Observe(play.Obs());

        play.Territory = 621; play.Teleported = true; play.Arrival = "Lochs - Ala Mhigan Quarter"; play.Pos = new Vector3(0, 0, 0);
        rec.Observe(play.Obs());
        Talk(rec, play, 1020356, new Vector3(5, 0, 5));

        var step = Assert.Single(rec.Finish()!.Block(1)!.Steps);
        Assert.Equal("Lochs - Ala Mhigan Quarter", step.AetheryteShortcut);
        Assert.Equal(621u, step.TerritoryId);
    }

    [Fact]
    public void Crossing_a_zone_line_on_foot_records_a_walk_out_of_the_old_zone()
    {
        var (rec, play) = Start();
        play.Accepted = true; play.Seq = 1; rec.Observe(play.Obs());
        play.Pos = new Vector3(500, 0, 0); rec.Observe(play.Obs());
        play.Territory = 401; play.Pos = new Vector3(-500, 0, 0); rec.Observe(play.Obs());

        var step = Assert.Single(rec.Finish()!.Block(1)!.Steps);
        Assert.Equal(StepKind.WalkTo, step.Kind);
        Assert.Equal(400u, step.TerritoryId);
        Assert.Equal(401u, step.TargetTerritoryId);
        Assert.Equal(new Vector3(500, 0, 0), step.Position);
    }

    [Fact]
    public void Combat_is_one_step_from_first_pull_to_last_kill_with_the_enemies_targeted()
    {
        var (rec, play) = Start();
        play.Accepted = true; play.Seq = 1; rec.Observe(play.Obs());

        play.InCombat = true; play.Target = 4015; play.TargetIsEnemy = true; play.TargetPos = play.Pos;
        rec.Observe(play.Obs());
        play.Target = 4016; rec.Observe(play.Obs());
        play.Target = 4015; rec.Observe(play.Obs());
        play.InCombat = false; play.Target = null; play.Vars = [0, 0, 0, 0, 0, 128];
        for (var i = 0; i < 10; i++) rec.Observe(play.Obs());

        var step = Assert.Single(rec.Finish()!.Block(1)!.Steps);
        Assert.Equal(StepKind.Combat, step.Kind);
        Assert.Equal(new List<uint> { 4015, 4016 }, step.KillEnemyDataIds);
        Assert.Equal(2, step.MinimumKillCount);
        Assert.Equal(new byte?[] { null, null, null, null, null, 128 }, step.CompletionQuestVariablesFlags);
    }

    [Fact]
    public void Entering_an_instance_records_a_duty_step_of_the_right_kind()
    {
        var (rec, play) = Start();
        play.Accepted = true; play.Seq = 1; rec.Observe(play.Obs());

        play.Target = 1016034; play.TargetPos = play.Pos; rec.Observe(play.Obs());
        play.InDuty = true; play.Cfc = 300; play.DutySolo = true; play.Territory = 999; rec.Observe(play.Obs());
        play.InDuty = false; play.Territory = 400; rec.Observe(play.Obs());
        play.InDuty = true; play.Cfc = 247; play.DutySolo = false; play.Territory = 998; rec.Observe(play.Obs());

        var steps = rec.Finish()!.Block(1)!.Steps;
        var solo = steps.Single(s => s.Kind == StepKind.SinglePlayerDuty);
        Assert.Equal(300u, solo.ContentFinderConditionId);
        Assert.Equal(1016034u, solo.DataId);
        var duty = steps.Single(s => s.Kind == StepKind.Duty);
        Assert.Equal(247u, duty.ContentFinderConditionId);
    }

    [Fact]
    public void Manual_walk_to_here_adds_a_waypoint_in_the_current_block()
    {
        var (rec, play) = Start();
        play.Accepted = true; play.Seq = 3; play.Pos = new Vector3(1, 2, 3); rec.Observe(play.Obs());
        rec.AddWalkToHere();
        var step = Assert.Single(rec.Finish()!.Block(3)!.Steps);
        Assert.Equal(StepKind.WalkTo, step.Kind);
        Assert.Equal(new Vector3(1, 2, 3), step.Position);
    }

    [Fact]
    public void Nothing_is_recorded_before_begin_or_after_finish()
    {
        var rec = new PathRecorder();
        var play = new Play();
        rec.Observe(play.Obs());
        Assert.False(rec.IsRecording);
        Assert.Null(rec.Finish());
    }
}
