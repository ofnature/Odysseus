using Odysseus.Windows.Dock;
using Xunit;

namespace Odysseus.Tests;

/// <summary>
/// Several plugins' edge docks on one edge used to centre themselves independently and draw on top of each
/// other. They now publish to one shared list and stack as a group. Charon carries the same rules.
/// </summary>
public class DockLayoutTests
{
    private const long Now = 100_000;
    private const float WorkTop = 0f;
    private const float WorkHeight = 1000f;

    private static DockSlot Slot(string owner, float height, int order, int edge = 0, long seen = Now)
        => new(owner, edge, height, order, seen);

    [Fact]
    public void AloneADockIsCentred()
    {
        var top = DockLayout.TopFor("Daedalus", 0, 100f, [Slot("Daedalus", 100f, 10)], Now, WorkTop, WorkHeight);
        Assert.Equal(450f, top);
    }

    /// <summary>Two docks on one edge: one group of 100 + gap + 200, centred, in order.</summary>
    [Fact]
    public void TwoDocksOnOneEdgeStackInsteadOfOverlapping()
    {
        DockSlot[] slots = [Slot("Charon", 200f, 20), Slot("Daedalus", 100f, 10)];
        var total = 100f + DockLayout.GroupGap + 200f;
        var groupTop = (WorkHeight - total) / 2f;

        Assert.Equal(groupTop, DockLayout.TopFor("Daedalus", 0, 100f, slots, Now, WorkTop, WorkHeight));
        Assert.Equal(groupTop + 100f + DockLayout.GroupGap, DockLayout.TopFor("Charon", 0, 200f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>Docks on different edges do not push each other.</summary>
    [Fact]
    public void DocksOnOppositeEdgesAreEachCentred()
    {
        DockSlot[] slots = [Slot("Daedalus", 100f, 10, edge: 0), Slot("Charon", 200f, 20, edge: 1)];
        Assert.Equal(450f, DockLayout.TopFor("Daedalus", 0, 100f, slots, Now, WorkTop, WorkHeight));
        Assert.Equal(400f, DockLayout.TopFor("Charon", 1, 200f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>A dock that stopped publishing (closed, unloaded) drops out and the rest close up.</summary>
    [Fact]
    public void AStaleDockDropsOut()
    {
        DockSlot[] slots = [Slot("Daedalus", 100f, 10, seen: Now - DockLayout.StaleMs - 1), Slot("Charon", 200f, 20)];
        Assert.Equal(400f, DockLayout.TopFor("Charon", 0, 200f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>A dock with no tabs this frame takes no room.</summary>
    [Fact]
    public void AnEmptyDockTakesNoRoom()
    {
        DockSlot[] slots = [Slot("Daedalus", 0f, 10), Slot("Charon", 200f, 20)];
        Assert.Equal(400f, DockLayout.TopFor("Charon", 0, 200f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>The same order on both: the owner's name settles it, so every plugin computes the same stack.</summary>
    [Fact]
    public void EqualOrdersAreSettledByName()
    {
        DockSlot[] slots = [Slot("Zeta", 100f, 10), Slot("Alpha", 100f, 10)];
        Assert.True(DockLayout.TopFor("Alpha", 0, 100f, slots, Now, WorkTop, WorkHeight)
                    < DockLayout.TopFor("Zeta", 0, 100f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>Taller than the screen: start at the top rather than above it.</summary>
    [Fact]
    public void AGroupTallerThanTheScreenStartsAtTheTop()
    {
        DockSlot[] slots = [Slot("Daedalus", 800f, 10), Slot("Charon", 800f, 20)];
        Assert.Equal(WorkTop, DockLayout.TopFor("Daedalus", 0, 800f, slots, Now, WorkTop, WorkHeight));
    }

    /// <summary>Not in the list (the share was unavailable): centre alone, as before.</summary>
    [Fact]
    public void MissingFromTheListMeansCentredAlone()
        => Assert.Equal(450f, DockLayout.TopFor("Daedalus", 0, 100f, [Slot("Charon", 200f, 20)], Now, WorkTop, WorkHeight));

    [Fact]
    public void TheShareTagIsTheOneCharonUses() => Assert.Equal("EdgeDock.Layout.v1", DockLayoutShare.Tag);

    /// <summary>Odysseus (30) stacks after Daedalus (10) and Charon (20) on a shared edge.</summary>
    [Fact]
    public void Odysseus_stacks_below_Daedalus_and_Charon()
    {
        DockSlot[] slots = [Slot("Odysseus", 90f, 30), Slot("Charon", 200f, 20), Slot("Daedalus", 100f, 10)];
        var daedalus = DockLayout.TopFor("Daedalus", 0, 100f, slots, Now, WorkTop, WorkHeight);
        var odysseus = DockLayout.TopFor("Odysseus", 0, 90f, slots, Now, WorkTop, WorkHeight);
        Assert.Equal(daedalus + 100f + DockLayout.GroupGap + 200f + DockLayout.GroupGap, odysseus);
    }
}
