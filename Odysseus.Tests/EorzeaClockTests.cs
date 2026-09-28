using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

/// <summary>Timed nodes: Eorzea time from the real clock, and when a window is next up.</summary>
public class EorzeaClockTests
{
    /// <summary>One Eorzean hour is 175 real seconds from the epoch, so an Eorzean day is 70 minutes.</summary>
    private static DateTime AtEorzea(int day, int hour, int minute)
        => DateTime.UnixEpoch.AddSeconds(((day * 24 + hour) * 60 + minute) * 175.0 / 60.0);

    /// <summary>Grade 3 Shroud Topsoil's unspoiled node: 06:00 for three Eorzean hours.</summary>
    private static readonly NodeWindow[] Topsoil = [new(NodeWindow.FromHhmm(600), NodeWindow.FromHhmm(300))];

    [Fact]
    public void The_sheet_writes_times_as_hours_and_minutes()
    {
        Assert.Equal(360, NodeWindow.FromHhmm(600));
        Assert.Equal(180, NodeWindow.FromHhmm(300));
        Assert.Equal(90, NodeWindow.FromHhmm(130));
    }

    [Fact]
    public void Eorzea_time_runs_off_the_real_clock()
        => Assert.Equal(6 * 60 + 30, EorzeaClock.MinuteOfDay(AtEorzea(20_000, 6, 30)), precision: 3);

    [Fact]
    public void Before_its_window_it_counts_down_to_the_opening()
    {
        var next = EorzeaClock.Next(Topsoil, AtEorzea(20_000, 5, 0))!.Value;
        Assert.False(next.Up);
        Assert.Equal(175, next.Change.TotalSeconds, precision: 1);       // one Eorzean hour
        Assert.Equal("in 2:55", EorzeaClock.Describe(Topsoil, AtEorzea(20_000, 5, 0)));
    }

    [Fact]
    public void Inside_its_window_it_counts_down_to_the_close()
    {
        var next = EorzeaClock.Next(Topsoil, AtEorzea(20_000, 7, 0))!.Value;
        Assert.True(next.Up);
        Assert.Equal(2 * 175, next.Change.TotalSeconds, precision: 1);   // two Eorzean hours left
        Assert.StartsWith("up · 5:50 left", EorzeaClock.Describe(Topsoil, AtEorzea(20_000, 7, 0)));
    }

    [Fact]
    public void After_its_window_the_wait_runs_to_tomorrow()
    {
        var next = EorzeaClock.Next(Topsoil, AtEorzea(20_000, 10, 0))!.Value;
        Assert.False(next.Up);
        Assert.Equal(20 * 175, next.Change.TotalSeconds, precision: 1);  // 10:00 → 06:00 is twenty hours
    }

    [Fact]
    public void A_window_across_midnight_is_up_on_both_sides_of_it()
    {
        NodeWindow[] late = [new(NodeWindow.FromHhmm(2200), NodeWindow.FromHhmm(400))];
        Assert.True(EorzeaClock.Next(late, AtEorzea(20_000, 1, 0))!.Value.Up);
        Assert.True(EorzeaClock.Next(late, AtEorzea(20_000, 23, 0))!.Value.Up);
        Assert.False(EorzeaClock.Next(late, AtEorzea(20_000, 3, 0))!.Value.Up);
    }

    [Fact]
    public void An_item_on_an_always_there_node_has_no_timer()
        => Assert.Equal(string.Empty, EorzeaClock.Describe([], DateTime.UtcNow));
}
