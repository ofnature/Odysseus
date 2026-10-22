using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>Quest accept, reward choice and complete when questing by hand — never while a run has the windows.</summary>
public class QuestWindowHelperTests
{
    private readonly FakeStepWorld _world = new();
    private readonly QuestWindowHelper _helper;

    public QuestWindowHelperTests() => _helper = new QuestWindowHelper(_world);

    private void Tick(bool enabled = true, bool running = false)
    {
        _helper.Tick(enabled, running, _world.UtcNow);
        _world.Advance(1.1);
    }

    [Fact]
    public void An_offer_is_accepted_and_said()
    {
        _world.VisibleAddons.Add("JournalAccept");
        Tick();
        Assert.Contains("AcceptQuestOffer", _world.Calls);
        Assert.StartsWith("accepted a quest", _helper.Status);
    }

    /// <summary>Complete greyed for a choice: the reward is picked, then Complete goes through.</summary>
    [Fact]
    public void A_reward_window_is_picked_then_completed()
    {
        _world.VisibleAddons.Add("JournalResult");
        _world.RewardCompleteEnabled = false;
        _world.RewardPick = "Gil × 500 — gil";
        Tick();
        Assert.Contains("PickReward", _world.Calls);
        Assert.StartsWith("picked Gil", _helper.Status);
        Tick();
        Assert.DoesNotContain("JournalResult", _world.VisibleAddons);   // the fake closes it on Complete
        Assert.StartsWith("completed a quest", _helper.Status);
    }

    [Fact]
    public void Nothing_is_pressed_while_a_run_has_the_windows_or_when_off()
    {
        _world.VisibleAddons.Add("JournalAccept");
        Tick(running: true);
        Assert.StartsWith("idle", _helper.Status);
        Tick(enabled: false);
        Assert.Equal("off", _helper.Status);
        Assert.DoesNotContain("AcceptQuestOffer", _world.Calls);
    }

    [Fact]
    public void A_reward_needing_a_choice_with_picking_off_says_so()
    {
        _world.VisibleAddons.Add("JournalResult");
        _world.RewardCompleteEnabled = false;   // RewardPick null: picking off
        Tick();
        Assert.Contains("needs a choice", _helper.Status);
    }
}
