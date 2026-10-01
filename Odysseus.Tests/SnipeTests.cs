using System.Numerics;
using Odysseus.Services.Paths;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// Sniping sections — Securing the Saltery (2970) and 31 others: interact with the rifle, then the
/// shots are taken inside the event — skipped by the snipe hook (ported from CBT), or by the player.
/// </summary>
public class SnipeTests
{
    private static QuestStep Snipe() => new()
    {
        Kind = StepKind.Snipe, KindName = "Snipe", DataId = 1024052, TerritoryId = 400, Position = Vector3.Zero,
    };

    private static void Ticks(StepExecutor ex, FakeStepWorld w, int n, double s = 0.5)
    {
        for (var i = 0; i < n && ex.Status == StepStatus.Running; i++) { ex.Tick(); w.Advance(s); }
    }

    [Fact]
    public void A_snipe_step_is_supported_now()
        => Assert.True(StepExecutor.IsSupported(StepKind.Snipe));

    [Fact]
    public void The_snipe_skip_is_switched_on_for_the_section_and_back_off_after()
    {
        var w = new FakeStepWorld { AutoSnipeEnabled = false };
        w.Spawned.Add(1024052);
        var ex = new StepExecutor(w);
        ex.Begin(Snipe());

        Assert.Contains("AutoSnipe True", w.Calls);
        Ticks(ex, w, 40);
        Assert.Equal(StepStatus.Done, ex.Status);
        Assert.Contains(w.Calls, c => c.StartsWith("Interact"));
        Assert.Equal(false, w.AutoSnipeEnabled);                         // put back as it was
    }

    [Fact]
    public void A_snipe_skip_already_on_is_left_on()
    {
        var w = new FakeStepWorld { AutoSnipeEnabled = true };
        w.Spawned.Add(1024052);
        var ex = new StepExecutor(w);
        ex.Begin(Snipe());
        Ticks(ex, w, 40);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("AutoSnipe"));
    }

    /// <summary>No snipe skip this patch: the player is told once, and a slow hand at the rifle is not a dialogue that never ended.</summary>
    [Fact]
    public void Without_the_snipe_skip_the_player_takes_the_shots_and_the_step_waits_for_them()
    {
        var w = new FakeStepWorld();
        w.Spawned.Add(1024052);
        var ex = new StepExecutor(w);
        ex.Begin(Snipe());
        Assert.Single(w.Calls, c => c.StartsWith("Notify") && c.Contains("sniping section"));

        Ticks(ex, w, 4);
        w.IsOccupied = true;                                              // in the scope
        Ticks(ex, w, 400);                                                // 200s — past the dialogue budget
        Assert.Equal(StepStatus.Running, ex.Status);

        w.IsOccupied = false;                                             // the section is over
        Ticks(ex, w, 20);
        Assert.Equal(StepStatus.Done, ex.Status);
    }

    [Theory]
    [InlineData("Assign Cu Sith to first battlehorn, summon, then interact with J'yhuh Tia", 1, "Cu Sith")]
    [InlineData("Go into the crypt below, capture Geshunpest, assign to Second Battlehorn, summon", null, null)]
    [InlineData("Capture Geshunpest, assign Geshunpest to the Second Battlehorn, summon", 2, "Geshunpest")]
    public void A_battlehorn_comment_is_read(string comment, int? slot, string? pet)
    {
        var ask = StepExecutor.BattlehornAsk(comment);
        Assert.Equal(slot, ask?.Slot);
        Assert.Equal(pet, ask?.Pet);
    }

    /// <summary>
    /// The Wilds Call (5491): a manual step in the data — assign Cu Sith to the first battlehorn,
    /// summon, talk to J'yhuh Tia. Run: walk there, assign, summon, interact.
    /// </summary>
    [Fact]
    public void The_wilds_call_assigns_summons_and_talks()
    {
        var w = new FakeStepWorld { TerritoryId = 148, ArriveOnMove = true };
        w.Spawned.Add(1059323);
        w.Actions["First Battlehorn"] = 44890;
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.WaitForManualProgress, KindName = "WaitForManualProgress", DataId = 1059323, TerritoryId = 148,
            Position = new Vector3(119.9f, -7f, -87.5f),
            Comment = "Assign Cu Sith to first battlehorn, summon, then interact with J'yhuh Tia",
        });
        Ticks(ex, w, 40);

        var assign = w.Calls.IndexOf("Battlehorn 1 Cu Sith");
        var summon = w.Calls.IndexOf("UseAction 44890");
        var talk = w.Calls.IndexOf("Interact 1059323");
        Assert.True(assign >= 0 && summon > assign && talk > summon, string.Join(" | ", w.Calls));
    }

    /// <summary>Already on the right battlehorn: not picked again (that would free it).</summary>
    [Fact]
    public void A_pet_already_on_its_battlehorn_is_left_there()
    {
        var w = new FakeStepWorld { TerritoryId = 148, ArriveOnMove = true };
        w.Spawned.Add(1059323);
        w.Actions["First Battlehorn"] = 44890;
        w.BattlehornPets[1] = "Cu Sith";
        var ex = new StepExecutor(w);
        ex.Begin(new QuestStep
        {
            Kind = StepKind.WaitForManualProgress, KindName = "WaitForManualProgress", DataId = 1059323, TerritoryId = 148,
            Position = new Vector3(119.9f, -7f, -87.5f),
            Comment = "Assign Cu Sith to first battlehorn, summon, then interact with J'yhuh Tia",
        });
        Ticks(ex, w, 40);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Battlehorn"));
        Assert.Contains("UseAction 44890", w.Calls);
    }
}
