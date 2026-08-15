using Odysseus.Services.Run;

namespace Odysseus.Tests;

public class RunStateTests
{
    [Theory]
    [InlineData(RunState.Idle, false)]
    [InlineData(RunState.Faulted, false)]
    [InlineData(RunState.Select, true)]
    [InlineData(RunState.Travel, true)]
    [InlineData(RunState.Step, true)]
    [InlineData(RunState.Combat, true)]
    [InlineData(RunState.Handoff, true)]
    [InlineData(RunState.Advance, true)]
    [InlineData(RunState.Reconcile, true)]
    public void IsDriving_is_true_for_every_state_that_owns_the_character(RunState state, bool expected)
    {
        // This value is published on Odysseus.IsBusy and decides when Daedalus fights for us —
        // getting it wrong is either "rotation never fires" or "rotation fires while idle".
        Assert.Equal(expected, state.IsDriving());
    }
}
