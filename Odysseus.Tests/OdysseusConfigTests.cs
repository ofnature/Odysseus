using Odysseus.Config;

namespace Odysseus.Tests;

public class OdysseusConfigTests
{
    [Fact]
    public void Ships_dark()
    {
        // A quest runner that starts walking the character on install is a bad neighbour.
        Assert.False(new OdysseusConfig().Enabled);
    }

    [Fact]
    public void Resume_is_on_and_silent_by_default()
    {
        var config = new OdysseusConfig();
        Assert.True(config.EnableResume);
        Assert.False(config.ConfirmBeforeResume);
    }

    [Fact]
    public void Handoffs_are_on_by_default()
    {
        // The whole point of building Theseus first: instanced content goes to the plugin that does it.
        var config = new OdysseusConfig();
        Assert.True(config.HandOffSoloDuties);
        Assert.True(config.HandOffDutiesToTheseus);
    }

    [Fact]
    public void Fleet_is_publish_only_with_a_stale_timeout()
    {
        var config = new OdysseusConfig();
        Assert.True(config.PublishFleetStatus);
        Assert.True(config.PeerStaleSeconds > 0f);
    }
}
