using System.Numerics;
using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class DialogueStagehandTests
{
    [Fact]
    public void The_subtitle_box_is_advanced_whenever_it_is_up()
    {
        var w = new FakeStepWorld();
        var hand = new DialogueStagehand();
        hand.Tick(w);
        Assert.DoesNotContain("AdvanceTalk", w.Calls);

        w.VisibleAddons.Add("Talk");
        hand.Tick(w);
        Assert.Contains("AdvanceTalk", w.Calls);
    }

    [Fact]
    public void The_skip_prompt_is_answered_yes_only_during_a_cutscene()
    {
        var w = new FakeStepWorld();
        w.VisibleAddons.Add("SelectString");
        w.ListEntries.AddRange(["Yes.", "No."]);
        var hand = new DialogueStagehand();

        hand.Tick(w);                                  // not in a cutscene: an ordinary list is not ours
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Select "));

        w.InCutscene = true;
        hand.Tick(w);
        Assert.Contains("Select 0", w.Calls);

        // Throttled: the window closing takes a beat, and a second press picks blind.
        hand.Tick(w);
        Assert.Equal(1, w.Calls.Count(c => c.StartsWith("Select ")));
    }

    /// <summary>Watching cutscenes: the skip prompt only comes from the player's own Esc, and the answer is theirs.</summary>
    [Fact]
    public void Watching_cutscenes_leaves_the_skip_prompt_to_the_player()
    {
        var w = new FakeStepWorld { InCutscene = true };
        w.VisibleAddons.Add("SelectString");
        w.VisibleAddons.Add("Talk");
        w.ListEntries.AddRange(["Yes.", "No."]);
        var hand = new DialogueStagehand { SkipCutscenes = () => false };

        hand.Tick(w);
        Assert.DoesNotContain(w.Calls, c => c.StartsWith("Select "));
        Assert.Contains("AdvanceTalk", w.Calls);   // dialogue still advances
    }
}
