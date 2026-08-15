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
    public void Unresolvable_list_choice_leaves_the_menu_alone_and_says_why()
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
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Select "));
        Assert.Contains(w.Calls, c => c.StartsWith("Log") && c.Contains("could not be resolved"));
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
