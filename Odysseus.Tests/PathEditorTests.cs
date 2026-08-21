using Odysseus.Windows;

namespace Odysseus.Tests;

public class PathEditorTests
{
    [Fact]
    public void A_waypoint_inserted_before_takes_the_steps_place_and_pushes_it_down()
    {
        // Quest 1217's sequence 1 is a single Interact that will not path. The waypoint has to go
        // in front of it — "after" would put it past the interaction it was meant to reach.
        Assert.Equal(0, PathEditorWindow.InsertIndex(selectedStep: 0, stepCount: 1, before: true));
        Assert.Equal(1, PathEditorWindow.InsertIndex(selectedStep: 0, stepCount: 1, before: false));

        Assert.Equal(2, PathEditorWindow.InsertIndex(selectedStep: 2, stepCount: 5, before: true));
        Assert.Equal(3, PathEditorWindow.InsertIndex(selectedStep: 2, stepCount: 5, before: false));
    }

    [Fact]
    public void Nothing_selected_lands_at_the_ends_rather_than_out_of_range()
    {
        Assert.Equal(0, PathEditorWindow.InsertIndex(selectedStep: -1, stepCount: 0, before: true));
        Assert.Equal(0, PathEditorWindow.InsertIndex(selectedStep: -1, stepCount: 0, before: false));
        Assert.Equal(3, PathEditorWindow.InsertIndex(selectedStep: 9, stepCount: 3, before: true));
        Assert.Equal(3, PathEditorWindow.InsertIndex(selectedStep: 9, stepCount: 3, before: false));
    }
}
