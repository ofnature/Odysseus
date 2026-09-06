using Odysseus.Services.Gathering;

namespace Odysseus.Tests;

public class GatherHomeTests
{
    /// <summary>The commands are load-bearing strings read out of Lifestream 2.5.4.16 and the game's general actions.</summary>
    [Fact]
    public void Every_destination_maps_to_the_command_that_goes_there()
    {
        Assert.Null(GatherHomeCommands.For(GatherHome.Stay));
        Assert.Equal("/generalaction Return", GatherHomeCommands.For(GatherHome.Return));
        Assert.Equal("/li inn", GatherHomeCommands.For(GatherHome.Inn));
        Assert.Equal("/li auto", GatherHomeCommands.For(GatherHome.Estate));
        Assert.Equal("/li fc", GatherHomeCommands.For(GatherHome.FreeCompany));
        Assert.Equal("/li home", GatherHomeCommands.For(GatherHome.Private));
        Assert.Equal("/li apt", GatherHomeCommands.For(GatherHome.Apartment));
        Assert.All(Enum.GetValues<GatherHome>(), h => Assert.NotEmpty(GatherHomeCommands.Label(h)));
    }
}
