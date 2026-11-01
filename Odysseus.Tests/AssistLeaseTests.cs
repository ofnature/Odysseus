using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>Dialogue and hand-over lent to another plugin (Parcae) — only while leased, never over a run of ours.</summary>
public class AssistLeaseTests
{
    private readonly FakeStepWorld _world = new();
    private readonly AssistLease _assist = new();

    private void Tick(bool running = false, double seconds = 0.6)
    {
        _assist.Tick(_world, running, _world.UtcNow);
        _world.Advance(seconds);
    }

    [Fact]
    public void Without_a_lease_nothing_is_pressed()
    {
        _world.VisibleAddons.Add("Talk");
        _world.VisibleAddons.Add("Request");
        for (var i = 0; i < 5; i++) Tick();
        Assert.DoesNotContain("AdvanceTalk", _world.Calls);
        Assert.DoesNotContain("HandOver", _world.Calls);
    }

    [Fact]
    public void A_lease_advances_talk_and_hands_over_once_settled()
    {
        Assert.True(_assist.Lease("Parcae", 60, _world.UtcNow));
        _world.VisibleAddons.Add("Talk");
        Tick();
        Assert.Contains("AdvanceTalk", _world.Calls);

        _world.VisibleAddons.Remove("Talk");
        _world.VisibleAddons.Add("Request");
        Tick();
        Assert.DoesNotContain("HandOver", _world.Calls);   // the window is still filling in
        Tick();
        Assert.Contains("HandOver", _world.Calls);
        Assert.DoesNotContain("Request", _world.VisibleAddons);
    }

    [Fact]
    public void A_hand_in_the_bags_cannot_cover_is_left_alone()
    {
        _assist.Lease("Parcae", 60, _world.UtcNow);
        _world.CanSatisfyHandOver = false;
        _world.VisibleAddons.Add("Request");
        for (var i = 0; i < 6; i++) Tick();
        Assert.DoesNotContain("HandOver", _world.Calls);
    }

    [Fact]
    public void The_confirm_right_after_hand_over_is_answered_but_a_later_yes_no_is_not()
    {
        _assist.Lease("Parcae", 60, _world.UtcNow);
        _world.VisibleAddons.Add("Request");
        Tick();
        Tick();
        _world.VisibleAddons.Add("SelectYesno");
        Tick();
        Assert.Contains("YesNo True", _world.Calls);

        _world.Calls.Clear();
        _world.Advance(10);
        Tick();
        Assert.DoesNotContain("YesNo True", _world.Calls);
    }

    [Fact]
    public void A_run_of_ours_keeps_its_own_windows()
    {
        _assist.Lease("Parcae", 60, _world.UtcNow);
        _world.VisibleAddons.Add("Talk");
        _world.VisibleAddons.Add("Request");
        for (var i = 0; i < 4; i++) Tick(running: true);
        Assert.DoesNotContain("AdvanceTalk", _world.Calls);
        Assert.DoesNotContain("HandOver", _world.Calls);
    }

    [Fact]
    public void A_lease_lapses_unless_renewed_and_zero_releases_it()
    {
        var now = _world.UtcNow;
        Assert.True(_assist.Lease("Parcae", 60, now));
        Assert.True(_assist.IsActive(now.AddSeconds(59)));
        Assert.False(_assist.IsActive(now.AddSeconds(61)));

        _assist.Lease("Parcae", 9999, now);   // capped at five minutes
        Assert.True(_assist.IsActive(now.AddSeconds(299)));
        Assert.False(_assist.IsActive(now.AddSeconds(301)));

        _assist.Lease("Parcae", 0, now);
        Assert.False(_assist.IsActive(now));
        Assert.False(_assist.Lease("", 60, now));
    }

    [Fact]
    public void One_caller_releasing_leaves_another_callers_lease()
    {
        var now = _world.UtcNow;
        _assist.Lease("Parcae", 60, now);
        _assist.Lease("Other", 60, now);
        _assist.Lease("Parcae", 0, now);
        Assert.True(_assist.IsActive(now));
        Assert.Equal(["Other"], _assist.Holders(now));
    }
}
