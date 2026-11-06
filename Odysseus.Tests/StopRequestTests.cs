using Odysseus.Services.Run;

namespace Odysseus.Tests;

/// <summary>
/// Another plugin's stop after the run (Odysseus.RequestStopAfterRun, Argus before it moves the
/// character): taken only while busy, never a toggle, spent once idle, and stopped only where the
/// character is free.
/// </summary>
public class StopRequestTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
    private readonly StopRequest _stop = new();

    [Fact]
    public void Asked_while_idle_it_says_no_and_leaves_nothing_behind()
    {
        Assert.False(_stop.Request(busy: false));
        Assert.False(_stop.Requested);   // no stale flag to stop the next start
    }

    [Fact]
    public void Asked_while_busy_it_says_yes_and_asking_again_is_not_a_toggle()
    {
        Assert.True(_stop.Request(busy: true));
        Assert.True(_stop.Request(busy: true));
        Assert.True(_stop.Requested);
    }

    [Fact]
    public void It_is_spent_once_nothing_is_busy_and_a_fresh_start_clears_it()
    {
        _stop.Request(busy: true);
        Assert.False(_stop.Settle(busy: true));   // still stopping
        Assert.True(_stop.Requested);
        Assert.True(_stop.Settle(busy: false));
        Assert.False(_stop.Requested);
        Assert.False(_stop.Settle(busy: false));  // nothing left to spend

        _stop.Request(busy: true);
        _stop.Clear();
        Assert.False(_stop.Requested);
    }

    [Fact]
    public void Free_stops_once_it_has_stayed_free_a_moment()
    {
        Assert.Equal(StopVerdict.Hold, _stop.At(false, false, false, T0));
        Assert.Equal(StopVerdict.Hold, _stop.At(false, false, false, T0 + StopRequest.FreeSettle / 2));
        // A cutscene opening a beat late starts the settle over.
        Assert.Equal(StopVerdict.Hold, _stop.At(false, false, true, T0 + StopRequest.FreeSettle));
        Assert.Equal(StopVerdict.Hold, _stop.At(false, false, false, T0 + StopRequest.FreeSettle * 2));
        Assert.Equal(StopVerdict.Stop, _stop.At(false, false, false, T0 + StopRequest.FreeSettle * 3));
    }

    [Fact]
    public void A_fight_is_waited_out_and_a_duty_is_carried_through()
    {
        Assert.Equal(StopVerdict.Hold, _stop.At(false, true, false, T0));
        Assert.Equal(StopVerdict.Hold, _stop.At(false, true, false, T0.AddMinutes(10)));
        Assert.Equal(StopVerdict.Carry, _stop.At(true, false, false, T0.AddMinutes(10)));
    }

    [Fact]
    public void A_window_is_waited_on_for_a_while_then_the_run_carries_on()
    {
        Assert.Equal(StopVerdict.Hold, _stop.At(false, false, true, T0));
        Assert.Equal(StopVerdict.Hold, _stop.At(false, false, true, T0 + StopRequest.WindowHoldMax - TimeSpan.FromSeconds(1)));
        Assert.Equal(StopVerdict.Carry, _stop.At(false, false, true, T0 + StopRequest.WindowHoldMax));
        // The next point between steps waits afresh.
        Assert.Equal(StopVerdict.Hold, _stop.At(false, false, true, T0 + StopRequest.WindowHoldMax * 2));
    }
}
