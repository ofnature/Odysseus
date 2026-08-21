using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Quest;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class DialogueTests
{
    private sealed class Texts : IDialogueTexts
    {
        public Dictionary<(ushort, string), string> Map { get; } = new();
        public string? Resolve(ushort questId, string key) => Map.TryGetValue((questId, key), out var t) ? t : null;
    }

    private static QuestStep Interact(uint dataId) => new()
    {
        Kind = StepKind.Interact, KindName = "Interact", DataId = dataId, TerritoryId = 400, Position = Vector3.Zero,
    };

    private static void Ticks(StepExecutor ex, FakeStepWorld w, int n, double s = 0.5)
    {
        for (var i = 0; i < n && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(s); }
    }

    [Fact]
    public void FindEntry_prefers_exact_then_contains()
    {
        var entries = new[] { "Invite Varis to explain himself.", "Say nothing of Zenos or the Ascians.", "Nothing." };
        Assert.Equal(1, StepExecutor.FindEntry(entries, "Say nothing of Zenos or the Ascians."));
        Assert.Equal(0, StepExecutor.FindEntry(entries, "invite varis to explain himself"));
        Assert.Equal(-1, StepExecutor.FindEntry(entries, "Attack."));
    }

    [Fact]
    public void An_interaction_that_opened_nothing_is_asked_again()
    {
        // A sprint keybind firing on the same frame eats the keypress: the target is there, the
        // interact goes out, and no conversation ever opens.
        var w = new FakeStepWorld { TalksWhenInteracted = false };
        w.Spawned.Add(7);
        var ex = new StepExecutor(w);
        ex.Begin(Interact(7));
        Ticks(ex, w, 30);

        Assert.Equal(3, w.Calls.Count(c => c == "Interact 7")); // the first, then two more
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("asking again"));
        Assert.Equal(StepStatus.Done, ex.Status); // and it still gives up rather than looping
    }

    [Fact]
    public void An_interaction_from_out_of_reach_walks_to_the_npc_rather_than_pressing_again()
    {
        // Brotherhood of Ash seq 3: the walk finished on the lip above the NPC, inside the step's
        // stop distance and nowhere near close enough to talk. Three presses, nothing opened.
        var w = new FakeStepWorld { TalksWhenInteracted = false, ArriveOnMove = false };
        w.Spawned.Add(1005578);
        w.Positions[1005578] = new Vector3(0, -9, 0); // nine yalms below us
        var ex = new StepExecutor(w);
        ex.Begin(Interact(1005578));
        Ticks(ex, w, 20);

        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("walking to it before asking again"));
        Assert.Contains(w.Calls, c => c.StartsWith("Move") || c.StartsWith("MoveClose"));
        Assert.Equal(1, w.Calls.Count(c => c == "Interact 1005578")); // not pressed again from up there

        // Once we are actually next to it, the interaction is asked again and lands.
        w.PlayerPosition = new Vector3(0, -9, 0);
        w.TalksWhenInteracted = true;
        Ticks(ex, w, 20);
        Assert.Equal(2, w.Calls.Count(c => c == "Interact 1005578"));
    }

    [Fact]
    public void A_flight_that_ended_over_the_npcs_head_lands_before_trying_again()
    {
        // Questionable flies you to a great many of these, and a flight that finishes above the
        // NPC can never talk to them: interacting from the air does nothing at all.
        var w = new FakeStepWorld { TalksWhenInteracted = false, ArriveOnMove = false, IsMounted = true };
        w.Spawned.Add(1005578);
        w.Positions[1005578] = new Vector3(0, -20, 0);
        var ex = new StepExecutor(w);
        ex.Begin(Interact(1005578));
        Ticks(ex, w, 20);

        Assert.Contains("Dismount", w.Calls);
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("dismounting first"));
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("walking to it"));
    }

    [Fact]
    public void The_descent_is_waited_out_rather_than_pressed_through()
    {
        // Dismounting in the air is a fall, and you are still Mounted all the way down. Both
        // retries used to be spent halfway to the ground.
        var w = new FakeStepWorld { TalksWhenInteracted = false, IsMounted = true, HoldsMount = true };
        w.Spawned.Add(7);
        var ex = new StepExecutor(w);
        ex.Begin(Interact(7));
        Ticks(ex, w, 30); // fifteen seconds of falling

        var pressedWhileFalling = w.Calls.Count(c => c == "Interact 7");
        Assert.True(w.Calls.Count(c => c == "Dismount") > 1, "kept asking on the way down");

        // Feet on the ground: now it interacts.
        w.HoldsMount = false;
        w.IsMounted = false;
        w.TalksWhenInteracted = true;
        Ticks(ex, w, 6);
        Assert.Equal(pressedWhileFalling + 1, w.Calls.Count(c => c == "Interact 7"));
    }

    [Fact]
    public void The_npc_is_faced_before_being_talked_to()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(7);
        var ex = new StepExecutor(w);
        ex.Begin(Interact(7));
        Ticks(ex, w, 10);

        var faced = w.Calls.FindIndex(c => c == "Face 7");
        var talked = w.Calls.FindIndex(c => c == "Interact 7");
        Assert.True(faced >= 0 && talked > faced, string.Join(" | ", w.Calls));
    }

    [Fact]
    public void An_interaction_that_did_open_something_is_left_alone()
    {
        var w = new FakeStepWorld(); // talks when interacted, as the game does
        w.Spawned.Add(7);
        var ex = new StepExecutor(w);
        ex.Begin(Interact(7));
        Ticks(ex, w, 30);

        Assert.Equal(1, w.Calls.Count(c => c == "Interact 7"));
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void List_choice_is_answered_by_resolved_text()
    {
        var texts = new Texts();
        texts.Map[(3182, "TEXT_STMBDF104_03182_A1_000_001")] = "Invite Varis to explain himself.";
        var w = new FakeStepWorld();
        w.Spawned.Add(7);
        var ex = new StepExecutor(w, texts);
        var step = Interact(7);
        step.DialogueChoices = [new DialogueChoice("List", "TEXT_STMBDF104_03182_Q1_000_000", "TEXT_STMBDF104_03182_A1_000_001", null)];
        ex.Begin(step, questId: 3182);
        Ticks(ex, w, 3);

        w.IsOccupied = true;
        w.VisibleAddons.Add("SelectString");
        w.ListEntries.AddRange(["Say nothing of Zenos or the Ascians.", "Invite Varis to explain himself."]);
        ex.Tick(); w.Advance(0.5);
        ex.Tick();
        Assert.Equal(1, w.Calls.Count(c => c == "Select 1")); // answered once, not every tick
    }

    [Fact]
    public void Unresolvable_list_choice_takes_the_first_option_and_says_why()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(7);
        var ex = new StepExecutor(w, new Texts());
        var step = Interact(7);
        step.DialogueChoices = [new DialogueChoice("List", "Q", "A", null)];
        ex.Begin(step, questId: 1);
        Ticks(ex, w, 3);
        w.IsOccupied = true;
        w.VisibleAddons.Add("SelectString");
        w.ListEntries.Add("Something");
        ex.Tick();
        Assert.Contains(w.Calls, c => c == "Select 0");
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("could not be resolved"));
    }

    [Fact]
    public void A_list_the_step_never_named_is_answered_after_a_grace()
    {
        // Quest 2601's three townspeople each open "what will you say?" and the path data names
        // none of them. Left alone the player stays occupied and the step never ends.
        var w = new FakeStepWorld();
        w.Spawned.Add(7);
        var ex = new StepExecutor(w, new Texts());
        ex.Begin(Interact(7), questId: 2601);
        Ticks(ex, w, 3);

        w.IsOccupied = true;
        w.VisibleAddons.Add("SelectString");
        w.ListEntries.AddRange(["Ask about their strengths first.", "Task them with a finished product."]);
        ex.Tick();
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Select ")); // TextAdvance gets first refusal

        w.Advance(4);
        ex.Tick();
        Assert.Equal(1, w.Calls.Count(c => c == "Select 0"));
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("does not name"));

        // Answered, the conversation ends and the step goes on to the next NPC.
        w.IsOccupied = false;
        w.VisibleAddons.Remove("SelectString");
        w.Advance(1);
        ex.Tick();
        Assert.Equal(StepStatus.Done, ex.Tick());
    }

    [Fact]
    public void An_empty_list_window_is_waited_on_rather_than_answered_blind()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(7);
        var ex = new StepExecutor(w, new Texts());
        var step = Interact(7);
        step.DialogueChoices = [new DialogueChoice("List", "Q", "A", null)];
        ex.Begin(step, questId: 1);
        Ticks(ex, w, 3);
        w.IsOccupied = true;
        w.VisibleAddons.Add("SelectString"); // up, but its entries have not filled in
        ex.Tick();
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Select "));

        w.ListEntries.Add("Something");
        ex.Tick();
        Assert.Contains(w.Calls, c => c == "Select 0");
    }

    [Fact]
    public void Reward_window_left_open_gets_completed_by_us_after_a_grace()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(7);
        var ex = new StepExecutor(w);
        var step = Interact(7);
        step.Kind = StepKind.CompleteQuest;
        ex.Begin(step, questId: 1);
        Ticks(ex, w, 3);

        // Hand-in dialogue, then the reward window appears and nobody presses Complete.
        w.IsOccupied = true;
        w.VisibleAddons.Add("JournalResult");
        Ticks(ex, w, 2);
        Assert.DoesNotContain("CompleteReward", w.Calls);   // grace: TextAdvance gets first go
        Ticks(ex, w, 6);
        Assert.Contains("CompleteReward", w.Calls);
        Assert.DoesNotContain("JournalResult", w.VisibleAddons); // the fake closes it on Complete
        Assert.Equal(StepStatus.Done, Run(ex, w));
    }

    [Fact]
    public void Reward_window_needing_a_choice_says_so_instead_of_timing_out_silently()
    {
        var w = new FakeStepWorld { RewardCompleteEnabled = false };
        w.Spawned.Add(7);
        var ex = new StepExecutor(w);
        var step = Interact(7);
        step.Kind = StepKind.CompleteQuest;
        ex.Begin(step, questId: 1);
        Ticks(ex, w, 3);
        w.IsOccupied = true;
        w.VisibleAddons.Add("JournalResult");
        Ticks(ex, w, 300, s: 1);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("reward window is waiting for a choice", ex.FailReason);
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("optional reward needs choosing"));
        Assert.Equal(1, w.Calls.Count(c => c.StartsWith("Log") && c.Contains("optional reward"))); // said once
    }

    private static StepStatus Run(StepExecutor ex, FakeStepWorld w)
    {
        for (var i = 0; i < 40 && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(0.5); }
        return ex.Status;
    }

    [Fact]
    public void Say_resolves_the_key_and_speaks_it()
    {
        var texts = new Texts();
        texts.Map[(1656, "TEXT_HEAVNA607_01656_SAYTODO_000")] = "Halone guide you.";
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w, texts);
        var step = new QuestStep { Kind = StepKind.Say, KindName = "Say", TerritoryId = 400, Position = Vector3.Zero, ChatMessageKey = "TEXT_HEAVNA607_01656_SAYTODO_000" };
        ex.Begin(step, questId: 1656);
        Ticks(ex, w, 10);
        Assert.Contains("Chat /say Halone guide you.", w.Calls);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Fact]
    public void Say_without_a_resolvable_key_fails_naming_it()
    {
        var w = new FakeStepWorld();
        var ex = new StepExecutor(w, new Texts());
        var step = new QuestStep { Kind = StepKind.Say, KindName = "Say", TerritoryId = 400, Position = Vector3.Zero, ChatMessageKey = "TEXT_X" };
        ex.Begin(step, questId: 5);
        Ticks(ex, w, 3);
        Assert.Equal(StepStatus.Failed, ex.Status);
        Assert.Contains("TEXT_X", ex.FailReason);
    }

    [Fact]
    public void Importer_keeps_the_say_key()
    {
        const string json = """
            { "QuestSequence": [ { "Sequence": 1, "Steps": [
              { "TerritoryId": 400, "InteractionType": "Say", "DataId": 5, "ChatMessage": { "Key": "TEXT_HEAVNA607_01656_SAYTODO_000" } } ] } ] }
            """;
        var path = QuestionableImporter.Parse("1656_x.json", "QuestPaths/x", json, out _)!;
        Assert.Equal("TEXT_HEAVNA607_01656_SAYTODO_000", path.Block(1)!.Steps[0].ChatMessageKey);
    }
}
