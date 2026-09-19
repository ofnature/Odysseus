using Odysseus.Services.Ipc;

namespace Odysseus.Tests;

public class ArtisanIpcTests
{
    /// <summary>
    /// Artisan's gate takes a ushort recipe id while the sheet's ids are uint — 38,500 at the top
    /// today against a 65,535 ceiling. A cast would wrap silently on the patch that crosses it and
    /// craft something else entirely, so the narrowing refuses and says so. The guard runs before
    /// anything touches Dalamud, which is why this needs no plugin interface.
    /// </summary>
    [Fact]
    public void A_recipe_id_past_what_Artisans_gate_carries_is_refused_out_loud()
    {
        var said = new List<string>();
        var artisan = new ArtisanIpc(null!, said.Add);

        Assert.False(artisan.CraftItem(ushort.MaxValue + 1u, 1));
        Assert.Contains(said, m => m.Contains("65535") && m.Contains("craft it yourself"));
    }
}
